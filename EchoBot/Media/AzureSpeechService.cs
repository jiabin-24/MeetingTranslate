using EchoBot.Constants;
using EchoBot.Translator;
using EchoBot.Util;
using EchoBot.WebRTC;
using EchoBot.WebSocket;
using MeetingTranscription.Models.Configuration;
using Microsoft.AspNetCore.SignalR;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.Translation;
using Microsoft.Extensions.Options;
using Microsoft.Graph.Communications.Common;
using Microsoft.Skype.Bots.Media;
using Newtonsoft.Json;
using Sprache;
using StackExchange.Redis;
using System.Buffers;
using System.Collections.Concurrent;
using System.Data;
using System.Runtime.InteropServices;
using static EchoBot.Models.Caption;

namespace EchoBot.Media
{
    /// <summary>
    /// Class AzureSpeechService.
    /// </summary>
    public class AzureSpeechService : BaseSpeechService
    {
        /// <summary>
        /// The is draining indicator
        /// </summary>
        protected bool _isDraining;

        private readonly CacheHelper _cacheHelper;

        // Mapping between audio socket Id and participant Id.
        private readonly ConcurrentDictionary<string, Models.Participant> _audioToIdentityMap = new();

        private int _readFromInstanceTimes = 0;

        private int _placeHolderIndex;

        // 每个流（key）上次发送的时间戳（毫秒）
        private static readonly ConcurrentDictionary<string, long> _lastSentAtMs = new();
        private const int MinIntervalMs = 500;

        private const string AUTO = "auto";

        private readonly AppSettings _appSettings;
        private readonly TranslatorOptions _translatorOptions;
        private readonly AudioOutputStream _audioOutputStream = AudioOutputStream.CreatePullStream();

        private readonly IHubContext<CaptionSignalRHub> _captionHub;
        private readonly RtcSessionManager _rtcSessionManager;
        private readonly ITranslatorClient _translatorClient;
        private readonly IConnectionMultiplexer _mux;
        private readonly string _threadId;
        // per-speaker streams and recognizers
        private readonly PushAudioInputStream _audioInputStream = AudioInputStream.CreatePushStream(AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1));
        private readonly ConcurrentDictionary<string, PushAudioInputStream> _audioInputStreamDic = [];
        private readonly Dictionary<string, TranslationRecognizer> _recognizerDic = [];
        private PhraseListGrammar _phraseList;

        /// <summary>
        /// Initializes a new instance of the <see cref="AzureSpeechService" /> class.
        /// </summary>
        public AzureSpeechService(AppSettings settings, string threadId = "")
        {
            _appSettings = settings;
            _translatorOptions = ServiceLocator.GetRequiredService<IOptions<TranslatorOptions>>().Value;
            _cacheHelper = ServiceLocator.GetRequiredService<CacheHelper>();
            _threadId = threadId ?? string.Empty;

            _translatorClient = ServiceLocator.GetRequiredService<ITranslatorClient>();
            _captionHub = ServiceLocator.GetRequiredService<IHubContext<CaptionSignalRHub>>();
            _rtcSessionManager = ServiceLocator.GetRequiredService<RtcSessionManager>();
            _mux = ServiceLocator.GetRequiredService<IConnectionMultiplexer>();
        }

        private async Task CreateRecognizer(string sourceLang)
        {
            var audioInputStream = AudioInputStream.CreatePushStream(AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1));
            var speechConfig = SpeechConfig(sourceLang);
            if (_appSettings.CustomSpeechEndpoints.TryGetValue(sourceLang, out string endpoint) && !string.IsNullOrEmpty(endpoint))
                speechConfig.EndpointId = endpoint;

            TranslationRecognizer recognizer;
            if (!sourceLang.Equals(AUTO))
                recognizer = new TranslationRecognizer(speechConfig, AudioConfig.FromStreamInput(audioInputStream));
            else
            {
                // prepare auto-detect config for recognizers
                var speechEndpoints = _appSettings.CustomSpeechEndpoints.ToDictionary(k => k.Key, endpoint => endpoint.Value == null ? SourceLanguageConfig.FromLanguage(endpoint.Key)
                    : SourceLanguageConfig.FromLanguage(endpoint.Key, endpoint.Value));
                var autoDetect = AutoDetectSourceLanguageConfig.FromSourceLanguageConfigs([.. speechEndpoints.Values]);
                recognizer = new TranslationRecognizer(speechConfig, autoDetect, AudioConfig.FromStreamInput(audioInputStream));
            }

            _recognizerDic[sourceLang] = recognizer;
            _audioInputStreamDic[sourceLang] = audioInputStream;

            _phraseList = PhraseListGrammar.FromRecognizer(recognizer);
            _phraseList.SetWeight(1.5);

            var stopRecognition = new TaskCompletionSource<int>();

            // wire events
            recognizer.Recognizing += async (s, e) => await Recognizer_Recognizing(s as TranslationRecognizer, e);
            recognizer.Recognized += async (s, e) => await Recognizer_Recognized(s as TranslationRecognizer, e);
            recognizer.Canceled += (s, e) =>
            {
                Logger.LogWarning("CANCELED: Reason={Reason}", e.Reason);
                if (e.Reason == CancellationReason.Error)
                {
                    Logger.LogError("CANCELED: ErrorCode={ErrorCode}", e.ErrorCode);
                    Logger.LogError("CANCELED: ErrorDetails={ErrorDetails}", e.ErrorDetails);
                    Logger.LogError("CANCELED: Did you update the subscription info?");
                }
                stopRecognition.TrySetResult(0);
            };
            recognizer.SessionStarted += async (s, e) => Logger.LogInformation("Session started event.");
            recognizer.SessionStopped += (s, e) =>
            {
                Logger.LogInformation("Session stopped event.\r\nStop recognition.");
                stopRecognition.TrySetResult(0);
            };

            // start continuous recognition
            await recognizer.StartContinuousRecognitionAsync().ConfigureAwait(false);
            Task.WaitAny([stopRecognition.Task]);

            // Stops recognition.
            await recognizer.StopContinuousRecognitionAsync().ConfigureAwait(false);

            _isDraining = false;
        }

        private SpeechTranslationConfig SpeechConfig(string sourceLang)
        {
            var speechConfig = SpeechTranslationConfig.FromSubscription(_appSettings.SpeechConfigKey, _appSettings.SpeechConfigRegion);
            if (!sourceLang.Equals(AUTO))
                speechConfig.SpeechRecognitionLanguage = sourceLang;

            _translatorOptions.Routing.Keys.ForEach(lang => speechConfig.AddTargetLanguage(lang.Split('-')[0]));
            // 提升识别准确率
            //speechConfig.SetProperty(PropertyId.SpeechServiceConnection_LanguageIdMode, "Continuous"); // 持续检测语言
            speechConfig.SetProperty(PropertyId.Speech_SegmentationSilenceTimeoutMs, "300"); // 让断句更短
            //speechConfig.SetProperty(PropertyId.SpeechServiceConnection_InitialSilenceTimeoutMs, "2000"); // 开头如果一直安静，到这个超时就跳过等待（适合尽快“进入状态”）
            speechConfig.SetProperty(PropertyId.SpeechServiceResponse_StablePartialResultThreshold, "1"); // 生成“稳定（不易回撤）”中间结果前所需的内部稳定度阈值，数字越小越“激进”
            //speechConfig.SetProperty("SpeechServiceResponse_ContinuousLanguageId_Priority", "Latency"); // 语言检测优先准确率
            speechConfig.SetProperty("SpeechServiceConnection_RecoModelType", "Enhanced"); // 使用增强模型
            speechConfig.SetProperty("SpeechServiceConnection_AlwaysRequireEnhancedSpeech", "false"); // 始终使用增强模型
            speechConfig.SetProperty("SpeechServiceResponse_PostProcessingOption", "TrueText"); // 使用 TrueText 后处理

            return speechConfig;
        }

        private async Task Recognizer_Recognizing(TranslationRecognizer? sender, TranslationRecognitionEventArgs e)
        {
            var speakerId = CurrentSpeakerId;
            var sourceLang = e.Result.Properties.GetProperty(PropertyId.SpeechServiceConnection_AutoDetectSourceLanguageResult);

            Logger.LogDebug("RECOGNIZING (speaker={speakerId}) in '{sourceLang}': Text={Text}", speakerId, sourceLang, e.Result.Text);

            try
            {
                var partialText = e.Result.Text;
                if (string.IsNullOrWhiteSpace(partialText))
                    return;

                if (!RecognizingInterval(speakerId, MinIntervalMs))
                    return;

                var translations = e.Result.Translations.ToDictionary(k => AppConstants.LangMap.FirstOrDefault(l => l.Key.StartsWith(k.Key)).Value ?? k.Key, v => v.Value);
                var captions = BuildTextDictionary(translations, sourceLang, partialText);
                _ = Transcript(captions, false, e.Offset, e.Result.Duration, sourceLang, partialText, speakerId);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to publish partial caption");
            }
        }

        // 添加自定义短语以提升识别准确率
        public override void AddPhrases(IEnumerable<string> phrases)
        {
            foreach (var p in phrases)
            {
                if (!string.IsNullOrWhiteSpace(p))
                    _phraseList.AddPhrase(p);
            }
        }

        // 是否可发送（满足时间窗口）
        private static bool RecognizingInterval(string key, int minIntervalMs)
        {
            var now = Environment.TickCount64; // 单调递增毫秒
            var last = _lastSentAtMs.GetOrAdd(key, 0L);

            // 未达到间隔：不发送
            if (now - last < minIntervalMs) return false;

            // 达到/超过间隔：更新并允许发送
            _lastSentAtMs[key] = now;
            return true;
        }

        private async Task Recognizer_Recognized(TranslationRecognizer? sender, TranslationRecognitionEventArgs e)
        {
            if (sender == null) return;

            var speakerId = CurrentSpeakerId;

            if (e.Result.Reason == ResultReason.TranslatedSpeech)
            {
                var original = e.Result.Text;
                if (string.IsNullOrEmpty(original)) return;

                var sourceLang = e.Result.Properties.GetProperty(PropertyId.SpeechServiceConnection_AutoDetectSourceLanguageResult);

                Logger.LogDebug("RECOGNIZED (speaker={speakerId}) in '{sourceLang}': Text={original}", speakerId, sourceLang, original);

                await BatchTranslateAsync(original, sourceLang, e.Offset, e.Result.Duration, speakerId);
            }
        }

        /// <summary>
        /// Appends the audio buffer.
        /// </summary>
        /// <param name="audioBuffer"></param>
        /// <param name="speakerId">Optional explicit speaker id to route to.</param>
        public override async Task AppendAudioBuffer(AudioMediaBuffer audioBuffer, string speakerId)
        {
            var sourceLangs = _appSettings.CustomSpeechEndpoints.Keys;

            if (!IsRunning)
            {
                Start();
                await Task.WhenAll(sourceLangs.Select(CreateRecognizer)).ConfigureAwait(false);
            }

            try
            {
                var bufferLength = audioBuffer.Length;
                if (bufferLength > 0)
                {
                    var buffer = new byte[bufferLength];
                    Marshal.Copy(audioBuffer.Data, buffer, 0, (int)bufferLength);

                    SetCurrentSpeaker(speakerId, buffer, bufferLength);

                    sourceLangs.ForEach(lang => _audioInputStreamDic[lang].Write(buffer));
                    //_audioInputStreamDic[AUTO].Write(buffer);
                }
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Exception happend writing to input stream");
            }
        }

        /// <summary>
        /// Ends this instance.
        /// </summary>
        /// <returns>Task.</returns>
        public override async Task ShutDownAsync()
        {
            if (!IsRunning) return;

            if (IsRunning)
            {
                foreach (var recognizer in _recognizerDic)
                {
                    await recognizer.Value.StopContinuousRecognitionAsync();
                    recognizer.Value.Dispose();
                }
                foreach (var audioInputStream in _audioInputStreamDic)
                {
                    audioInputStream.Value.Close();
                    audioInputStream.Value.Dispose();
                }

                _audioInputStream.Close();
                _audioInputStream.Dispose();
                _audioOutputStream.Dispose();
                IsRunning = false;
            }
        }

        /// <summary>
        /// Starts this instance.
        /// </summary>
        private void Start()
        {
            if (!IsRunning) { IsRunning = true; }
        }

        private async Task TextToSpeech(string text, string lang, string sourceLang, string speakerId)
        {
            await _rtcSessionManager.PlayText(_threadId, text, lang, sourceLang, speakerId).ConfigureAwait(false);
        }

        private async Task BatchTranslateAsync(string original, string sourceLang, ulong offset, TimeSpan duration, string audioId)
        {
            try
            {
                var translatorRules = _translatorOptions.Routing.ToDictionary(r => r.Key, r => r.Value.TryGetValue(sourceLang, out string? value) ? value : null);

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var translated = await _translatorClient.BatchTranslateAsync(original, translatorRules!, cts.Token);

                await Transcript(translated, true, offset, duration, sourceLang, original, audioId);
            }
            catch (Exception ex)
            {
                Logger.LogError("Translate failed: {Message}", ex.Message);
            }
        }

        private async Task Transcript(IReadOnlyDictionary<string, string> captions, bool isFinal, ulong offset, TimeSpan duration,
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
                MeetingId: _threadId,
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

                var listKey = $"list:{_threadId}";
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
            // determine speaker label from active speakers if available
            if (_readFromInstanceTimes++ > 100)
            {
                _audioToIdentityMap.Clear();
                _readFromInstanceTimes = 0;
            }

            _audioToIdentityMap.TryGetValue(audioId, out var speaker);
            speaker ??= await _cacheHelper.GetAsync<Models.Participant>($"{_threadId}-{audioId}");
            if (speaker != null)
                _audioToIdentityMap[audioId] = speaker;
            speaker ??= new Models.Participant { DisplayName = $"Speaker-{audioId}" };

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

        private Dictionary<string, string> BuildTextDictionary(IReadOnlyDictionary<string, string> captions, string sourceLang, string sourceText)
        {
            var dict = captions.ToDictionary(k => k.Key, v => v.Value);
            dict[sourceLang] = sourceText; // 注意：原文语言可能就是 zh-CN 或 en-US，看你的识别输出

            _placeHolderIndex++;
            _translatorOptions.Routing.Keys.ForEach(lang =>
            {
                if (!dict.ContainsKey(lang))
                    dict[lang] = $"Translating{new string('.', _placeHolderIndex % 4)}";
            });
            return dict;
        }
    }
}
