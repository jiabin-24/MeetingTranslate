using EchoBot.Translator;
using EchoBot.Util;
using EchoBot.WebSocket;
using MeetingTranscription.Models.Configuration;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.Extensions.Options;
using Microsoft.Graph.Communications.Common;
using Microsoft.Skype.Bots.Media;
using Newtonsoft.Json;
using Sprache;
using StackExchange.Redis;
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

        // Mapping between audio socket Id and participant Id.
        private readonly ConcurrentDictionary<string, Models.Participant> _audioToIdentityMap;

        private int _placeHolderIndex;

        /// <summary>
        /// The logger
        /// </summary>
        private readonly ILogger _logger;
        private readonly AppSettings _appSettings;
        private readonly TranslatorOptions _translatorOptions;
        private readonly AudioOutputStream _audioOutputStream = AudioOutputStream.CreatePullStream();

        private readonly SpeechConfig _speechConfig;
        private readonly SpeechSynthesizer _synthesizer;
        private readonly ICaptionPublisher _wsPublisher;
        private readonly ITranslatorClient _translatorClient;
        private readonly IConnectionMultiplexer _mux;
        private readonly string _threadId;
        // per-speaker streams and recognizers
        private readonly ConcurrentDictionary<string, PushAudioInputStream> _streamBySpeaker = new();
        private readonly ConcurrentDictionary<string, SpeechRecognizer> _recognizerBySpeaker = new();
        private readonly ConcurrentDictionary<SpeechRecognizer, string> _speakerByRecognizer = new();
        private readonly AutoDetectSourceLanguageConfig _autoDetect;

        /// <summary>
        /// Initializes a new instance of the <see cref="SpeechService" /> class.
        /// </summary>
        public SpeechService(AppSettings settings, ConcurrentDictionary<string, Models.Participant> audioToIdentityMap, string threadId = "")
        {
            _logger = ServiceLocator.GetRequiredService<ILogger<SpeechService>>();
            _speechConfig = SpeechConfig.FromSubscription(settings.SpeechConfigKey, settings.SpeechConfigRegion);
            // Use WAV PCM output so Bot media buffers can be created from the stream
            _speechConfig.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Riff16Khz16BitMonoPcm);
            _appSettings = settings;
            _translatorOptions = ServiceLocator.GetRequiredService<IOptions<TranslatorOptions>>().Value;
            _audioToIdentityMap = audioToIdentityMap;
            _threadId = threadId ?? string.Empty;

            // prepare auto-detect config for recognizers
            var speechEndpoints = _appSettings.CustomSpeechEndpoints.Select(endpoint => endpoint.Value == null ? SourceLanguageConfig.FromLanguage(endpoint.Key)
                : SourceLanguageConfig.FromLanguage(endpoint.Key, endpoint.Value)).ToArray();
            _autoDetect = AutoDetectSourceLanguageConfig.FromSourceLanguageConfigs(speechEndpoints);

            // 提升识别准确率
            _speechConfig.SetProperty(PropertyId.SpeechServiceConnection_LanguageIdMode, "Continuous"); // 持续检测语言
            _speechConfig.SetProperty(PropertyId.Speech_SegmentationSilenceTimeoutMs, "300"); // 让断句更短
            _speechConfig.SetProperty(PropertyId.SpeechServiceConnection_InitialSilenceTimeoutMs, "2000"); // 开头如果一直安静，到这个超时就跳过等待（适合尽快“进入状态”）
            _speechConfig.SetProperty(PropertyId.SpeechServiceConnection_EndSilenceTimeoutMs, "250"); // 一句话末尾静音到这个超时就判定结束（可进一步加快落句）
            _speechConfig.SetProperty(PropertyId.SpeechServiceResponse_StablePartialResultThreshold, "1"); // 生成“稳定（不易回撤）”中间结果前所需的内部稳定度阈值，数字越小越“激进”
            _speechConfig.SetProperty("SpeechServiceResponse_ContinuousLanguageId_Priority", "Latency"); // 语言检测优先准确率
            _speechConfig.SetProperty("SpeechServiceConnection_RecoModelType", "Enhanced"); // 使用增强模型
            _speechConfig.SetProperty("SpeechServiceConnection_AlwaysRequireEnhancedSpeech", "false"); // 始终使用增强模型
            //_speechConfig.SetProperty("SpeechServiceResponse_PostProcessingOption", "TrueText"); // 使用 TrueText 后处理
            
            _synthesizer = new SpeechSynthesizer(_speechConfig, AudioConfig.FromStreamOutput(_audioOutputStream));
            _translatorClient = ServiceLocator.GetRequiredService<ITranslatorClient>();
            _wsPublisher = ServiceLocator.GetRequiredService<ICaptionPublisher>();
            _mux = ServiceLocator.GetRequiredService<IConnectionMultiplexer>();
        }

        private async Task CreateRecognizerForSpeaker(string speakerId)
        {
            // create a dedicated push stream for this speaker
            var stream = AudioInputStream.CreatePushStream(AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1));
            var audioConfig = AudioConfig.FromStreamInput(stream);
            var recognizer = new SpeechRecognizer(_speechConfig, _autoDetect, audioConfig);

            // store mappings
            _streamBySpeaker[speakerId] = stream;
            _recognizerBySpeaker[speakerId] = recognizer;
            _speakerByRecognizer[recognizer] = speakerId;

            var stopRecognition = new TaskCompletionSource<int>();

            // wire events
            recognizer.Recognizing += (s, e) => Recognizer_Recognizing(s as SpeechRecognizer, e);
            recognizer.Recognized += (s, e) => Recognizer_Recognized(s as SpeechRecognizer, e);
            recognizer.Canceled += (s, e) =>
            {
                _logger.LogWarning($"CANCELED: Reason={e.Reason}");
                if (e.Reason == CancellationReason.Error)
                {
                    _logger.LogError($"CANCELED: ErrorCode={e.ErrorCode}");
                    _logger.LogError($"CANCELED: ErrorDetails={e.ErrorDetails}");
                    _logger.LogError($"CANCELED: Did you update the subscription info?");
                }
                stopRecognition.TrySetResult(0);
            };
            recognizer.SessionStarted += async (s, e) => _logger.LogInformation("Session started event.");
            recognizer.SessionStopped += (s, e) =>
            {
                _logger.LogInformation("Session stopped event.\r\nStop recognition.");
                stopRecognition.TrySetResult(0);
            };

            // start continuous recognition
            await recognizer.StartContinuousRecognitionAsync();
            Task.WaitAny([stopRecognition.Task]);

            // Stops recognition.
            await recognizer.StopContinuousRecognitionAsync().ConfigureAwait(false);
        }

        private void Recognizer_Recognizing(SpeechRecognizer? sender, SpeechRecognitionEventArgs e)
        {
            if (sender == null) return;
            if (!_speakerByRecognizer.TryGetValue(sender, out var speakerId)) speakerId = "";

            var sourceLang = e.Result.Properties.GetProperty(PropertyId.SpeechServiceConnection_AutoDetectSourceLanguageResult);
            _logger.LogInformation($"RECOGNIZING (speaker={speakerId}) in '{sourceLang}': Text={e.Result.Text}");

            try
            {
                var partialText = e.Result.Text;
                if (string.IsNullOrWhiteSpace(partialText))
                    return;

                var captions = BuildTextDictionary(new Dictionary<string, string> { { sourceLang, partialText } }, sourceLang, partialText);
                _ = Transcript(captions, false, e.Offset, e.Result.Duration, sourceLang, partialText, speakerId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to publish partial caption");
            }
        }

        private void Recognizer_Recognized(SpeechRecognizer? sender, SpeechRecognitionEventArgs e)
        {
            if (sender == null) return;
            if (!_speakerByRecognizer.TryGetValue(sender, out var speakerId)) speakerId = "";

            if (e.Result.Reason == ResultReason.RecognizedSpeech)
            {
                var original = e.Result.Text;
                if (string.IsNullOrEmpty(original)) return;

                var sourceLang = e.Result.Properties.GetProperty(PropertyId.SpeechServiceConnection_AutoDetectSourceLanguageResult);
                _logger.LogInformation($"RECOGNIZED (speaker={speakerId}) in '{sourceLang}': Text={original}");

                try
                {
                    _ = BatchTranslateAsync(original, sourceLang, e.Offset, e.Result.Duration, speakerId);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Translate failed: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Appends the audio buffer.
        /// </summary>
        /// <param name="audioBuffer"></param>
        /// <param name="speakerId">Optional explicit speaker id to route to.</param>
        public async Task AppendAudioBuffer(AudioMediaBuffer audioBuffer, string speakerId)
        {
            if (!_isRunning) { Start(); }

            try
            {
                var bufferLength = audioBuffer.Length;
                if (bufferLength > 0)
                {
                    var buffer = new byte[bufferLength];
                    Marshal.Copy(audioBuffer.Data, buffer, 0, (int)bufferLength);

                    if (!_streamBySpeaker.ContainsKey(speakerId))
                        await CreateRecognizerForSpeaker(speakerId);

                    if (_streamBySpeaker.TryGetValue(speakerId, out var pushStream))
                        pushStream.Write(buffer);
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
                // stop and dispose any per-speaker recognizers
                foreach (var kv in _recognizerBySpeaker)
                {
                    try { kv.Value.StopContinuousRecognitionAsync().Wait(1000); } catch { }
                    try { kv.Value.Dispose(); } catch { }
                }
                _recognizerBySpeaker.Clear();

                // close per-speaker push streams
                foreach (var kv in _streamBySpeaker)
                {
                    try { kv.Value.Close(); } catch { }
                    try { kv.Value.Dispose(); } catch { }
                }
                _streamBySpeaker.Clear();

                _audioOutputStream.Dispose();
                _synthesizer.Dispose();

                _isRunning = false;
            }
        }

        /// <summary>
        /// Starts this instance.
        /// </summary>
        private void Start()
        {
            if (!_isRunning)
            {
                _isRunning = true;
            }
        }

        private async Task TextToSpeech(string text, string lang, string speakerId)
        {
            // convert the text to speech
            SpeechSynthesisResult result = await _synthesizer.SpeakTextAsync(text);
            // take the stream of the result
            // create 20ms media buffers of the stream and send to the AudioSocket in the BotMediaStream
            using (var stream = AudioDataStream.FromResult(result))
            {
                try
                {
                    byte[] audioBytes;

                    if (result.AudioData != null && result.AudioData.Length > 0)
                        audioBytes = result.AudioData;
                    else
                    {
                        // Read full audio bytes from stream
                        stream.SetPosition(0);
                        using var ms = new MemoryStream();
                        var buffer = new byte[8192];
                        uint read;
                        while ((read = stream.ReadData(buffer)) > 0)
                        {
                            ms.Write(buffer, 0, (int)read);
                        }
                        audioBytes = ms.ToArray();
                    }

                    // Create bot media buffers from the WAV bytes
                    // compute header hex for debugging
                    var headerHex = string.Empty;
                    if (audioBytes.Length >= 12)
                        headerHex = string.Join(' ', audioBytes.Take(12).Select(b => b.ToString("x2")));

                    // content type is WAV PCM
                    var contentType = "audio/wav";
                    var audioId = Guid.NewGuid().ToString();
                    // publish to connected websocket clients (include length and header sample)
                    await _wsPublisher.PublishAudioAsync(_threadId, audioId, audioBytes, speakerId, lang, contentType, audioBytes.Length, headerHex);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to TextToSpeech");
                }
            }
        }

        private async Task BatchTranslateAsync(string original, string sourceLang, ulong offset, TimeSpan duration, string audioId)
        {
            var translatorRules = _translatorOptions.Routing.ToDictionary(r => r.Key, r => r.Value.TryGetValue(sourceLang, out string? value) ? value : null);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var translated = await _translatorClient.BatchTranslateAsync(original, translatorRules!, cts.Token);

            await Transcript(translated, true, offset, duration, sourceLang, original, audioId);
        }

        private async Task Transcript(IReadOnlyDictionary<string, string> captions, bool isFinal, ulong offset, TimeSpan duration,
            string sourceLang, string sourceText, string audioId)
        {
            long startMs = (long)(offset / 10_000UL); // 1ms = 10,000 ticks，offset 是针对 Speech Regonizer 识别的时差（不能用于多 Regonizer 的线性排序）
            long realStartMs = (DateTime.UtcNow - DateTime.UnixEpoch).Ticks / 10_000;
            long endMs = realStartMs + (long)duration.TotalMilliseconds;

            // Determine speaker based on the active speakers snapshot (updated when buffer energy exceeded threshold)
            var speaker = GetParticipant(audioId);
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

            // send the transcript to the websocket clients
            await _wsPublisher.PublishCaptionAsync(payload);

            if (!isFinal)
                return;

            var listKey = $"list:{_threadId}";
            await _mux.GetDatabase().ListRightPushAsync(listKey, JsonConvert.SerializeObject(payload));
            await _mux.GetDatabase().KeyExpireAsync(listKey, TimeSpan.FromHours(1));

            // For each available caption (language -> text), synthesize speech and publish in parallel
            await TextToSpeechBatch(captions.ToDictionary(k => k.Key, v => v.Value), speaker.Id);
        }

        private Models.Participant GetParticipant(string audioId)
        {
            // determine speaker label from active speakers if available
            var speaker = new Models.Participant { DisplayName = "Bot" };
            if (!_audioToIdentityMap.TryGetValue(audioId, out speaker))
                speaker = new Models.Participant { DisplayName = $"Speaker-{audioId}" };
            return speaker;
        }

        private async Task TextToSpeechBatch(Dictionary<string, string> captions, string speakerId)
        {
            try
            {
                var tasks = captions
                    .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                    .Select(kv => TextToSpeech(kv.Value, kv.Key, speakerId))
                    .ToArray();

                if (tasks.Length > 0)
                    await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                // Log any failures from the parallel synthesis tasks
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
    }
}
