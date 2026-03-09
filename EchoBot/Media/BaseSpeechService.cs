using EchoBot.Constants;
using EchoBot.Translator;
using EchoBot.Util;
using EchoBot.WebRTC;
using EchoBot.WebSocket;
using MeetingTranscription.Models.Configuration;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Microsoft.Graph.Communications.Calls;
using Microsoft.Graph.Communications.Common;
using Microsoft.Graph.Models;
using Microsoft.Skype.Bots.Media;
using Newtonsoft.Json;
using StackExchange.Redis;
using static EchoBot.Models.Caption;

namespace EchoBot.Media
{
    public abstract class BaseSpeechService
    {
        /// <summary>
        /// The is the indicator if the media stream is running
        /// </summary>
        protected bool IsRunning = false;

        protected readonly TranslatorOptions TranslatorOptions;

        // Energy threshold (RMS) above which we consider this buffer as active speech for assigning speaker id
        protected const double SpeakerEnergyThreshold = 800.0;

        private int _placeHolderIndex;

        protected string CurrentSpeakerId = string.Empty;

        /// <summary>
        /// 自动检测来源语言的标志
        /// </summary>
        protected abstract string AUTO { get; }

        protected readonly string ThreadId;

        internal List<IParticipant> Participants;

        // Mapping between audio socket Id and participant Id.
        private Dictionary<string, Models.Participant> AudioToIdentityMap => Participants.ToDictionary(AudioId, p => IdentityToParticipant(AudioId(p), p.Resource.Info.Identity.User));

        // Logger created for the runtime type so derived classes get a category with their actual type
        protected ILogger Logger;

        private readonly ITranslatorClient _translatorClient;

        private readonly IHubContext<CaptionSignalRHub> _captionHub;

        private readonly IConnectionMultiplexer _mux;

        public BaseSpeechService(string threadId, List<IParticipant> participants)
        {
            ThreadId = threadId;
            Participants = participants;
            TranslatorOptions = ServiceLocator.GetRequiredService<IOptions<TranslatorOptions>>().Value;
            Logger = ServiceLocator.GetRequiredService<ILoggerFactory>().CreateLogger(GetType().FullName ?? GetType().Name);

            _translatorClient = ServiceLocator.GetRequiredService<ITranslatorClient>();
            _captionHub = ServiceLocator.GetRequiredService<IHubContext<CaptionSignalRHub>>();
            _mux = ServiceLocator.GetRequiredService<IConnectionMultiplexer>();
        }

        public virtual async Task ShutDownAsync()
        {
            if (!IsRunning) return;

            foreach (var rtcSessionManager in RtcSessionManagerRegistry.UnregisterByThreadId(ThreadId))
                await rtcSessionManager.Dispose();
            AcsWebSocketHandlerRegistry.UnregisterByThreadId(ThreadId);
            IsRunning = false;
        }

        public abstract Task AppendAudioBuffer(AudioMediaBuffer audioBuffer, string speakerId);

        public abstract void AddPhrases(IEnumerable<string> phrases);

        public event EventHandler<MediaStreamEventArgs> SendMediaBuffer;

        public virtual void OnSendMediaBufferEventArgs(object sender, MediaStreamEventArgs e)
        {
            SendMediaBuffer?.Invoke(this, e);
        }

        protected async Task BatchTranslateAsync(string original, string sourceLang, ulong offset, TimeSpan duration, string audioId, Dictionary<string, string>? captions = null)
        {
            try
            {
                var transEndpoints = TranslatorOptions.Routing.ToDictionary(r => r.Key, r => r.Value.TryGetValue(sourceLang, out string? value) ? value : null);

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var translated = await _translatorClient.BatchTranslateAsync(original, sourceLang, transEndpoints!, cts.Token);

                await Transcript(Util.Utilities.ConcatDictionary(translated, captions), true, offset, duration, sourceLang, original, audioId);
            }
            catch (Exception ex)
            {
                Logger.LogError("Translate failed: {Message}", ex.Message);
            }
        }

        protected async Task Transcript(IReadOnlyDictionary<string, string> captions, bool isFinal, ulong offset, TimeSpan duration,
            string sourceLang, string sourceText, string audioId)
        {
            long startMs = (long)(offset / 10_000UL); // 1ms = 10,000 ticks，offset 是针对 Speech Regonizer 识别的时差（不能用于多 Regonizer 的线性排序）
            long endMs = startMs + (long)duration.TotalMilliseconds;
            long realStartMs = (DateTime.UtcNow - DateTime.UnixEpoch).Ticks / 10_000;
            sourceLang = AppConstants.LangMap.TryGetValue(sourceLang, out string? value) ? value : sourceLang.Split('-')[0];

            // Determine speaker based on the active speakers snapshot (updated when buffer energy exceeded threshold)
            var speaker = await GetParticipant(audioId);
            var payload = new CaptionPayload(
                Type: "caption",
                MeetingId: ThreadId,
                Speaker: speaker.DisplayName,
                SpeakerId: speaker.Id,
                SourceLang: sourceLang,
                Text: BuildTextDictionary(captions, sourceLang, sourceText),
                IsFinal: isFinal,
                StartMs: startMs,
                EndMs: endMs,
                RealStartMs: realStartMs
            );

            try
            {
                // Send the transcript to the websocket clients
                await _captionHub.Clients.Group($"{payload.MeetingId}").SendCoreAsync("caption", [payload], default);

                if (!isFinal)
                    return;

                var listKey = $"list:{ThreadId}";
                await _mux.GetDatabase().ListRightPushAsync(listKey, JsonConvert.SerializeObject(payload));
                await _mux.GetDatabase().KeyExpireAsync(listKey, TimeSpan.FromHours(1));

                // For each available caption (language -> text), synthesize speech and publish in parallel
                _ = TextToSpeechBatch(captions.ToDictionary(k => k.Key, v => v.Value), sourceLang, speaker.Id);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Transcript and send error");
            }
        }

        private async Task<Models.Participant> GetParticipant(string audioId)
        {
            AudioToIdentityMap.TryGetValue(audioId, out var speaker);
            speaker ??= new Models.Participant { Id = audioId, DisplayName = $"Speaker-{audioId}" };

            return speaker;
        }

        private async Task TextToSpeechBatch(Dictionary<string, string> captions, string sourceLang, string speakerId)
        {
            try
            {
                var tasks = captions
                    .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                    .Select(kv => TextToSpeech(kv.Value, kv.Key, sourceLang, speakerId))
                    .ToArray();

                if (tasks.Length > 0)
                    await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "One or more TextToSpeech tasks failed.");
            }
        }

        protected abstract Task TextToSpeech(string text, string lang, string sourceLang, string speakerId);

        protected async Task TextToSpeech(byte[] pcm, string lang, string sourceLang, string speakerId)
        {
            if (!sourceLang.Equals(lang) && !sourceLang.Equals(AUTO))
                return;

            var rtcSessionManager = RtcSessionManagerRegistry.TryGet(ThreadId, lang, out var manager) ? manager : null;
            await RtcSessionManager.EnsureGroupCallConnectionAsync(rtcSessionManager);

            var acsMediaWebSocket = AcsWebSocketHandlerRegistry.TryGet(ThreadId, lang, out var handler) ? handler : null;
            if (acsMediaWebSocket != null)
                await acsMediaWebSocket.PushTtsFrameAsync(pcm, CancellationToken.None);
        }

        // Compute RMS energy from a 16-bit PCM buffer
        private static double ComputeRmsFrom16BitPcm(byte[] buffer, long bufferLength)
        {
            if (buffer == null || bufferLength <= 1) return 0.0;

            long sampleCount = bufferLength / 2; // 16-bit samples
            if (sampleCount == 0) return 0.0;

            double sumSquares = 0.0;

            for (long i = 0; i < sampleCount; i++)
            {
                int offset = (int)(i * 2);
                short sample = (short)(buffer[offset] | (buffer[offset + 1] << 8));
                double normalized = sample; // keep in int16 domain to compute RMS
                sumSquares += normalized * normalized;
            }

            double meanSquares = sumSquares / sampleCount;
            double rms = Math.Sqrt(meanSquares);
            return rms;
        }

        protected bool SetCurrentSpeaker(string speakerId, byte[]? buffer, long bufferLength)
        {
            if (speakerId != null)
            {
                // Compute buffer energy (RMS) for 16-bit PCM and only assign speaker when above threshold
                try
                {
                    var rms = ComputeRmsFrom16BitPcm(buffer, bufferLength);
                    if (rms >= SpeakerEnergyThreshold)
                        CurrentSpeakerId = speakerId;

                    return rms >= SpeakerEnergyThreshold;
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Failed to compute audio energy, assigning speaker id by default");
                    CurrentSpeakerId = speakerId;
                }
            }
            return false;
        }

        protected Dictionary<string, string> BuildTextDictionary(IReadOnlyDictionary<string, string> captions, string sourceLang, string sourceText, string? translateText = null)
        {
            var dict = captions.ToDictionary(k => k.Key, v => v.Value);
            dict[sourceLang] = sourceText; // 注意：原文语言可能就是 zh-CN 或 en-US，看你的识别输出

            _placeHolderIndex++;

            TranslatorOptions.Routing.Keys.ForEach(lang =>
            {
                if (!dict.ContainsKey(lang))
                    dict[lang] = translateText ?? $"Translating{new string('.', _placeHolderIndex % 4)}";
            });
            return dict;
        }

        private static string AudioId(IParticipant participant) => participant.Resource.MediaStreams.FirstOrDefault(x => x.MediaType == Modality.Audio).SourceId;

        private static Models.Participant IdentityToParticipant(string sourceId, Identity? identity)
        {
            if (identity == null)
            {
                return new Models.Participant
                {
                    Id = sourceId,
                    DisplayName = string.Empty
                };
            }
            return new Models.Participant
            {
                Id = identity.Id!,
                DisplayName = identity?.DisplayName ?? string.Empty
            };
        }
    }
}
