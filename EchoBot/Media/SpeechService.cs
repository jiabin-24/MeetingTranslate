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
    /// Class SpeechService.
    /// </summary>
    public class SpeechService
    {
        /// <summary>
        /// The is the indicator if the media stream is running
        /// </summary>
        private bool _isRunning = false;

        /// <summary>
        /// The is draining indicator
        /// </summary>
        protected bool _isDraining;

        private readonly CacheHelper _cacheHelper;

        // Mapping between audio socket Id and participant Id.
        private readonly ConcurrentDictionary<string, Models.Participant> _audioToIdentityMap = new();

        private int _readFromInstanceTimes = 0;

        private int _placeHolderIndex;

        // Energy threshold (RMS) above which we consider this buffer as active speech for assigning speaker id
        private const double SpeakerEnergyThreshold = 500.0;

        // 每个流（key）上次发送的时间戳（毫秒）
        private static readonly ConcurrentDictionary<string, long> _lastSentAtMs = new();
        private const int MinIntervalMs = 500;

        private string _currentSpeakerId = string.Empty;

        /// <summary>
        /// The logger
        /// </summary>
        private readonly ILogger _logger;
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
        private readonly SpeechTranslationConfig _speechConfig;
        private TranslationRecognizer _recognizer;
        private PhraseListGrammar _phraseList;

        /// <summary>
        /// Initializes a new instance of the <see cref="SpeechService" /> class.
        /// </summary>
        public SpeechService(AppSettings settings, string threadId = "")
        {
            _logger = ServiceLocator.GetRequiredService<ILogger<SpeechService>>();
            _appSettings = settings;
            _translatorOptions = ServiceLocator.GetRequiredService<IOptions<TranslatorOptions>>().Value;
            _cacheHelper = ServiceLocator.GetRequiredService<CacheHelper>();
            _threadId = threadId ?? string.Empty;

            _speechConfig = SpeechTranslationConfig.FromSubscription(settings.SpeechConfigKey, settings.SpeechConfigRegion);
            _speechConfig.SpeechRecognitionLanguage = "zh-CN";
            _translatorOptions.Routing.Keys.ForEach(lang => _speechConfig.AddTargetLanguage(lang.Split('-')[0]));
            // 提升识别准确率
            //_speechConfig.SetProperty(PropertyId.SpeechServiceConnection_LanguageIdMode, "Continuous"); // 持续检测语言
            //_speechConfig.SetProperty(PropertyId.Speech_SegmentationSilenceTimeoutMs, "200"); // 让断句更短
            //_speechConfig.SetProperty(PropertyId.SpeechServiceConnection_InitialSilenceTimeoutMs, "2000"); // 开头如果一直安静，到这个超时就跳过等待（适合尽快“进入状态”）
            //_speechConfig.SetProperty(PropertyId.SpeechServiceConnection_EndSilenceTimeoutMs, "200"); // 一句话末尾静音到这个超时就判定结束（可进一步加快落句）
            _speechConfig.SetProperty(PropertyId.SpeechServiceResponse_StablePartialResultThreshold, "1"); // 生成“稳定（不易回撤）”中间结果前所需的内部稳定度阈值，数字越小越“激进”
            //_speechConfig.SetProperty("SpeechServiceResponse_ContinuousLanguageId_Priority", "Latency"); // 语言检测优先准确率
            _speechConfig.SetProperty("SpeechServiceConnection_RecoModelType", "Enhanced"); // 使用增强模型
            _speechConfig.SetProperty("SpeechServiceConnection_AlwaysRequireEnhancedSpeech", "false"); // 始终使用增强模型
            _speechConfig.SetProperty("SpeechServiceResponse_PostProcessingOption", "TrueText"); // 使用 TrueText 后处理

            _translatorClient = ServiceLocator.GetRequiredService<ITranslatorClient>();
            _captionHub = ServiceLocator.GetRequiredService<IHubContext<CaptionSignalRHub>>();
            _rtcSessionManager = ServiceLocator.GetRequiredService<RtcSessionManager>();
            _mux = ServiceLocator.GetRequiredService<IConnectionMultiplexer>();
        }

        private async Task CreateRecognizerForSpeaker()
        {
            // prepare auto-detect config for recognizers
            var speechEndpoints = _appSettings.CustomSpeechEndpoints.Select(endpoint => endpoint.Value == null ? SourceLanguageConfig.FromLanguage(endpoint.Key)
                : SourceLanguageConfig.FromLanguage(endpoint.Key, endpoint.Value)).ToArray();
            var autoDetect = AutoDetectSourceLanguageConfig.FromSourceLanguageConfigs(speechEndpoints);

            var audioConfig = AudioConfig.FromStreamInput(_audioInputStream);
            _recognizer = new TranslationRecognizer(_speechConfig, audioConfig);

            _phraseList = PhraseListGrammar.FromRecognizer(_recognizer);
            _phraseList.SetWeight(1.5);

            var stopRecognition = new TaskCompletionSource<int>();

            // wire events
            _recognizer.Recognizing += async (s, e) => await Recognizer_Recognizing(s as TranslationRecognizer, e);
            _recognizer.Recognized += async (s, e) => await Recognizer_Recognized(s as TranslationRecognizer, e);
            _recognizer.Canceled += (s, e) =>
            {
                _logger.LogWarning("CANCELED: Reason={Reason}", e.Reason);
                if (e.Reason == CancellationReason.Error)
                {
                    _logger.LogError("CANCELED: ErrorCode={ErrorCode}", e.ErrorCode);
                    _logger.LogError("CANCELED: ErrorDetails={ErrorDetails}", e.ErrorDetails);
                    _logger.LogError("CANCELED: Did you update the subscription info?");
                }
                stopRecognition.TrySetResult(0);
            };
            _recognizer.SessionStarted += async (s, e) => _logger.LogInformation("Session started event.");
            _recognizer.SessionStopped += (s, e) =>
            {
                _logger.LogInformation("Session stopped event.\r\nStop recognition.");
                stopRecognition.TrySetResult(0);
            };

            // start continuous recognition
            await _recognizer.StartContinuousRecognitionAsync().ConfigureAwait(false);
            Task.WaitAny([stopRecognition.Task]);

            // Stops recognition.
            await _recognizer.StopContinuousRecognitionAsync().ConfigureAwait(false);

            _isDraining = false;
        }

        private async Task Recognizer_Recognizing(TranslationRecognizer? sender, TranslationRecognitionEventArgs e)
        {
            var speakerId = _currentSpeakerId;
            var sourceLang = e.Result.Properties.GetProperty(PropertyId.SpeechServiceConnection_AutoDetectSourceLanguageResult);

            _logger.LogDebug("RECOGNIZING (speaker={speakerId}) in '{sourceLang}': Text={Text}", speakerId, sourceLang, e.Result.Text);

            try
            {
                var partialText = e.Result.Text;
                if (string.IsNullOrWhiteSpace(partialText))
                    return;

                if (!RecognizingInterval(speakerId, MinIntervalMs))
                    return;

                var translations = e.Result.Translations.ToDictionary(k => "zh".Equals(k.Key) ? "zh-Hans" : k.Key, v => v.Value); // 这里做一个特殊处理，后续有更好的办法可以统一语言标签
                var captions = BuildTextDictionary(translations, sourceLang, partialText);
                _ = Transcript(captions, false, e.Offset, e.Result.Duration, sourceLang, partialText, speakerId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to publish partial caption");
            }
        }

        public void AddPhrases(IEnumerable<string> phrases)
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

            var speakerId = _currentSpeakerId;

            if (e.Result.Reason == ResultReason.TranslatedSpeech)
            {
                var original = e.Result.Text;
                if (string.IsNullOrEmpty(original)) return;

                var sourceLang = e.Result.Properties.GetProperty(PropertyId.SpeechServiceConnection_AutoDetectSourceLanguageResult);

                _logger.LogDebug("RECOGNIZED (speaker={speakerId}) in '{sourceLang}': Text={original}", speakerId, sourceLang, original);

                await BatchTranslateAsync(original, sourceLang, e.Offset, e.Result.Duration, speakerId);
            }
        }

        /// <summary>
        /// Appends the audio buffer.
        /// </summary>
        /// <param name="audioBuffer"></param>
        /// <param name="speakerId">Optional explicit speaker id to route to.</param>
        public async Task AppendAudioBuffer(AudioMediaBuffer audioBuffer, string speakerId)
        {
            if (!_isRunning)
            {
                Start();
                await CreateRecognizerForSpeaker();
            }

            try
            {
                var bufferLength = audioBuffer.Length;
                if (bufferLength > 0)
                {
                    var buffer = new byte[bufferLength];
                    Marshal.Copy(audioBuffer.Data, buffer, 0, (int)bufferLength);

                    if (speakerId != null)
                    {
                        // Compute buffer energy (RMS) for 16-bit PCM and only assign speaker when above threshold
                        try
                        {
                            var rms = ComputeRmsFrom16BitPcm(buffer, bufferLength);
                            if (rms >= SpeakerEnergyThreshold)
                                _currentSpeakerId = speakerId;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Failed to compute audio energy, assigning speaker id by default");
                            _currentSpeakerId = speakerId;
                        }
                    }

                    _audioInputStream.Write(buffer);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Exception happend writing to input stream");
            }
        }

        public virtual void OnSendMediaBufferEventArgs(object sender, MediaStreamEventArgs e)
        {
            SendMediaBuffer?.Invoke(this, e);
        }

        public event EventHandler<MediaStreamEventArgs> SendMediaBuffer;

        /// <summary>
        /// Ends this instance.
        /// </summary>
        /// <returns>Task.</returns>
        public async Task ShutDownAsync()
        {
            if (!_isRunning)
            {
                return;
            }

            if (_isRunning)
            {
                await _recognizer.StopContinuousRecognitionAsync();
                _recognizer.Dispose();
                _audioInputStream.Close();

                _audioInputStream.Dispose();
                _audioOutputStream.Dispose();

                _isRunning = false;
            }
        }

        /// <summary>
        /// Starts this instance.
        /// </summary>
        private void Start()
        {
            if (!_isRunning) { _isRunning = true; }
        }

        private async Task TextToSpeech(string text, string lang, string speakerId)
        {
            await _rtcSessionManager.PlayText(_threadId, text, lang, speakerId).ConfigureAwait(false);
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
                _logger.LogError("Translate failed: {Message}", ex.Message);
            }
        }

        private async Task Transcript(IReadOnlyDictionary<string, string> captions, bool isFinal, ulong offset, TimeSpan duration,
            string sourceLang, string sourceText, string audioId)
        {
            long startMs = (long)(offset / 10_000UL); // 1ms = 10,000 ticks，offset 是针对 Speech Regonizer 识别的时差（不能用于多 Regonizer 的线性排序）
            long endMs = startMs + (long)duration.TotalMilliseconds;
            long realStartMs = (DateTime.UtcNow - DateTime.UnixEpoch).Ticks / 10_000;

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
                await _captionHub.Clients.Group(payload.MeetingId).SendCoreAsync("caption", [payload], default);

                if (!isFinal)
                    return;

                var listKey = $"list:{_threadId}";
                await _mux.GetDatabase().ListRightPushAsync(listKey, JsonConvert.SerializeObject(payload));
                await _mux.GetDatabase().KeyExpireAsync(listKey, TimeSpan.FromHours(1));

                // For each available caption (language -> text), synthesize speech and publish in parallel
                _ = TextToSpeechBatch(captions.ToDictionary(k => k.Key, v => v.Value), speaker.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Transcript and send error");
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

        private async Task TextToSpeechBatch(Dictionary<string, string> captions, string speakerId)
        {
            try
            {
                var tasks = captions.Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                    .Select(kv => TextToSpeech(kv.Value, kv.Key, speakerId)).ToArray();

                if (tasks.Length > 0)
                    await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "One or more TextToSpeech tasks failed.");
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
    }
}
