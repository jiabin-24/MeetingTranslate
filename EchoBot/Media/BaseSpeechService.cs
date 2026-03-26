using EchoBot.Bot;
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

        private int _placeHolderIndex;

        protected string CurrentSpeakerId = string.Empty;

        /// <summary>
        /// 自动检测来源语言的标志
        /// </summary>
        protected abstract string AUTO { get; }

        protected readonly string ThreadId;

        private readonly CallHandler _callHandler;

        private Dictionary<string, Models.Participant> AudioToIdentityMap => _callHandler.Call.Participants.ToDictionary(CallHandler.GetIdentityId, IdentityToParticipant);

        protected ILogger Logger;

        private readonly ITranslatorClient _translatorClient;

        private readonly IHubContext<CaptionSignalRHub> _captionHub;

        private readonly IConnectionMultiplexer _mux;

        private readonly CacheHelper _cacheHelper;

        public BaseSpeechService(string threadId, CallHandler callHandler)
        {
            ThreadId = threadId;
            _callHandler = callHandler;
            TranslatorOptions = ServiceLocator.GetRequiredService<IOptions<TranslatorOptions>>().Value;
            Logger = ServiceLocator.GetRequiredService<ILoggerFactory>().CreateLogger(GetType().FullName ?? GetType().Name);

            _translatorClient = ServiceLocator.GetRequiredService<ITranslatorClient>();
            _captionHub = ServiceLocator.GetRequiredService<IHubContext<CaptionSignalRHub>>();
            _mux = ServiceLocator.GetRequiredService<IConnectionMultiplexer>();
            _cacheHelper = ServiceLocator.GetRequiredService<CacheHelper>();
        }

        public virtual async Task ShutDownAsync()
        {
            if (!IsRunning) return;

            foreach (var rtcSessionManager in RtcSessionManagerRegistry.UnregisterByThreadId(ThreadId))
                await rtcSessionManager.Dispose();
            AcsWebSocketHandlerRegistry.UnregisterByThreadId(ThreadId);
            IsRunning = false;
        }

        public virtual Task ShutDownSessionAsync(string speakerId)
        {
            return Task.CompletedTask;
        }

        public abstract Task AppendAudioBuffer(AudioMediaBuffer audioBuffer, string speakerId);

        public abstract void AddPhrases(IEnumerable<string> phrases);

        public event EventHandler<MediaStreamEventArgs> SendMediaBuffer;

        public virtual void OnSendMediaBufferEventArgs(object sender, MediaStreamEventArgs e)
        {
            SendMediaBuffer?.Invoke(this, e);
        }

        protected async Task BatchTranslateAsync(string original, string sourceLang, ulong offset, TimeSpan duration, string speakerId, Dictionary<string, string>? captions = null)
        {
            try
            {
                var transEndpoints = TranslatorOptions.Routing.ToDictionary(r => r.Key, r => r.Value.TryGetValue(sourceLang, out string? value) ? value : null);
                Dictionary<string, string> translated;
                if (TranslatorOptions.Enabled)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    translated = Util.Utilities.ConcatDictionary(captions ?? [], await _translatorClient.BatchTranslateAsync(original, sourceLang, transEndpoints!, cts.Token));
                }
                else
                {
                    // If translation is disabled, return the original text for all target languages.
                    translated = Util.Utilities.ConcatDictionary(transEndpoints.Keys.ToDictionary(to => to, to => original), captions);
                }

                await Transcript(translated, true, offset, duration, sourceLang, original, speakerId);
            }
            catch (Exception ex)
            {
                Logger.LogError("Translate failed: {Message}", ex.Message);
            }
        }

        protected async Task Transcript(IReadOnlyDictionary<string, string> captions, bool isFinal, ulong offset, TimeSpan duration,
            string sourceLang, string sourceText, string speakerId)
        {
            long startMs = (long)(offset / 10_000UL); // 1ms = 10,000 ticks，offset 是针对 Speech Regonizer 识别的时差（不能用于多 Regonizer 的线性排序）
            long endMs = startMs + (long)duration.TotalMilliseconds;
            long realStartMs = (DateTime.UtcNow - DateTime.UnixEpoch).Ticks / 10_000;
            sourceLang = AppConstants.LangMap.TryGetValue(sourceLang, out string? value) ? value : sourceLang.Split('-')[0];

            // Determine speaker based on the active speakers snapshot (updated when buffer energy exceeded threshold)
            var speaker = await GetParticipant(speakerId);
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

                var listKey = CacheConstants.MeetingCaptionKey(ThreadId);
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

        private async Task<Models.Participant> GetParticipant(string speakerId)
        {
            if (!AudioToIdentityMap.TryGetValue(speakerId, out var speaker))
            {
                speaker = new Models.Participant
                {
                    Id = speakerId,
                    DisplayName = $"Speaker-{speakerId}"
                };
            }

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

            var rtcSessionManager = RtcSessionManagerRegistry.TryRegister(ThreadId, lang, () => new RtcSessionManager(ThreadId, lang));
            if (!await RtcSessionManager.EnsureGroupCallConnectionAsync(rtcSessionManager))
                return;

            var acsMediaWebSocket = await AcsWebSocketHandlerRegistry.WaitForHandlerAsync(ThreadId, lang, TimeSpan.FromSeconds(10));
            if (acsMediaWebSocket == null)
                return;

            if (!await acsMediaWebSocket.WaitForTtsWriterAsync(TimeSpan.FromSeconds(10)))
                return;

            await acsMediaWebSocket.PushTtsFrameAsync(pcm, CancellationToken.None);
        }

        protected Dictionary<string, string> BuildTextDictionary(IReadOnlyDictionary<string, string> captions, string sourceLang, string sourceText)
        {
            var dict = captions.ToDictionary(k => k.Key, v => v.Value);
            dict[sourceLang] = sourceText; // 注意：原文语言可能就是 zh-CN 或 en-US，看你的识别输出

            _placeHolderIndex++;

            TranslatorOptions.Routing.Keys.ForEach(lang =>
            {
                if (!dict.ContainsKey(lang))
                    dict[lang] = $"Translating{new string('.', _placeHolderIndex % 4)}";
            });
            return dict;
        }

        private static Models.Participant IdentityToParticipant(IParticipant participant)
        {
            var identity = CallHandler.TryGetParticipantIdentity(participant);

            return new Models.Participant
            {
                Id = participant.Id,
                DisplayName = identity?.DisplayName ?? $"Speaker-{CallHandler.GetAudioSourceId(participant)}"
            };
        }
    }
}
