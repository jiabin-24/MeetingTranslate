using EchoBot.Util;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.Translation;
using Microsoft.Skype.Bots.Media;
using Newtonsoft.Json;
using StackExchange.Redis;
using System.Collections.Concurrent;
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
        private readonly ConcurrentDictionary<string, string> _audioToIdentityMap;

        /// <summary>
        /// The logger
        /// </summary>
        private readonly ILogger _logger;
        private readonly AppSettings _appSettings;
        private readonly PushAudioInputStream _audioInputStream = AudioInputStream.CreatePushStream(AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1));
        private readonly AudioOutputStream _audioOutputStream = AudioOutputStream.CreatePullStream();

        private readonly SpeechTranslationConfig _speechConfig;
        private TranslationRecognizer _recognizer;
        private readonly SpeechSynthesizer _synthesizer;
        private readonly CaptionPublisher _wsPublisher;
        private readonly IConnectionMultiplexer _mux;
        private readonly string _threadId;
        private uint[]? _activeSpeakers;

        /// <summary>
        /// Initializes a new instance of the <see cref="SpeechService" /> class.
        /// </summary>
        public SpeechService(AppSettings settings, ConcurrentDictionary<string, string> audioToIdentityMap, string threadId = "")
        {
            _logger = ServiceLocator.GetRequiredService<ILogger<SpeechService>>();
            _speechConfig = SpeechTranslationConfig.FromSubscription(settings.SpeechConfigKey, settings.SpeechConfigRegion);
            _appSettings = settings;
            _audioToIdentityMap = audioToIdentityMap;
            _threadId = threadId ?? string.Empty;

            // 添加目标语言
            settings.TargetLanguages.ForEach(lang => _speechConfig.AddTargetLanguage(lang));
            // 提升识别准确率
            _speechConfig.SetProperty(PropertyId.SpeechServiceConnection_LanguageIdMode, "Continuous");
            _speechConfig.SetProperty("SpeechServiceResponse_ContinuousLanguageId_Priority", "Accuracy");
            _speechConfig.SetProperty("SpeechServiceConnection_RecoModelType", "Enhanced");
            _speechConfig.SetProperty("SpeechServiceResponse_PostProcessingOption", "TrueText");
            _speechConfig.SetProperty("SpeechServiceConnection_InitialSilenceTimeoutMs", "7000");
            _speechConfig.SetProperty("SpeechServiceConnection_EndSilenceTimeoutMs", "2000");
            _speechConfig.SetProperty("SpeechServiceConnection_AlwaysRequireEnhancedSpeech", "true");

            _synthesizer = new SpeechSynthesizer(_speechConfig, AudioConfig.FromStreamOutput(_audioOutputStream));
            _wsPublisher = ServiceLocator.GetRequiredService<CaptionPublisher>();
            _mux = ServiceLocator.GetRequiredService<IConnectionMultiplexer>();
        }

        /// <summary>
        /// Appends the audio buffer.
        /// </summary>
        /// <param name="audioBuffer"></param>
        public async Task AppendAudioBuffer(AudioMediaBuffer audioBuffer)
        {
            // remember active speakers from this buffer so we can attribute transcripts
            if (audioBuffer.ActiveSpeakers != null && audioBuffer.ActiveSpeakers.Length > 0)
                _activeSpeakers = audioBuffer.ActiveSpeakers;

            if (!_isRunning)
            {
                Start();
                await ProcessSpeech();
            }

            try
            {
                // audio for a 1:1 call
                var bufferLength = audioBuffer.Length;
                if (bufferLength > 0)
                {
                    var buffer = new byte[bufferLength];
                    Marshal.Copy(audioBuffer.Data, buffer, 0, (int)bufferLength);

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

        /// <summary>
        /// Processes this instance.
        /// </summary>
        private async Task ProcessSpeech()
        {
            try
            {
                var stopRecognition = new TaskCompletionSource<int>();

                using (var audioInput = AudioConfig.FromStreamInput(_audioInputStream))
                {
                    if (_recognizer == null)
                    {
                        _logger.LogInformation("init recognizer");

                        // 自动检测源语言以及其目标语言对应的 custom speech endpoint
                        var speechEndpoints = _appSettings.CustomSpeechEndpoints.Select(endpoint => endpoint.Value == null ? SourceLanguageConfig.FromLanguage(endpoint.Key)
                            : SourceLanguageConfig.FromLanguage(endpoint.Key, endpoint.Value)).ToArray();
                        var autoDetect = AutoDetectSourceLanguageConfig.FromSourceLanguageConfigs(speechEndpoints);

                        _recognizer = new TranslationRecognizer(_speechConfig, autoDetect, audioInput);
                    }
                }

                _recognizer.Recognizing += (s, e) =>
                {
                    _logger.LogInformation($"RECOGNIZING: Text={e.Result.Text}");
                };

                _recognizer.Recognized += async (s, e) =>
                {
                    if (e.Result.Reason == ResultReason.TranslatedSpeech)
                    {
                        var original = e.Result.Text; // 原文
                        if (string.IsNullOrEmpty(original))
                            return;

                        var sourceLang = e.Result.Properties.GetProperty(PropertyId.SpeechServiceConnection_AutoDetectSourceLanguageResult);
                        _logger.LogInformation($"RECOGNIZED in '{sourceLang}': Text={original}");

                        await TextToSpeech(e.Result.Translations);
                        await Transcript(e.Result.Translations, e.Offset, e.Result.Duration, sourceLang, original);
                    }
                    else if (e.Result.Reason == ResultReason.NoMatch)
                    {
                        _logger.LogInformation($"NOMATCH: Speech could not be recognized.");
                    }
                };

                _recognizer.Canceled += (s, e) =>
                {
                    _logger.LogInformation($"CANCELED: Reason={e.Reason}");

                    if (e.Reason == CancellationReason.Error)
                    {
                        _logger.LogInformation($"CANCELED: ErrorCode={e.ErrorCode}");
                        _logger.LogInformation($"CANCELED: ErrorDetails={e.ErrorDetails}");
                        _logger.LogInformation($"CANCELED: Did you update the subscription info?");
                    }

                    stopRecognition.TrySetResult(0);
                };

                _recognizer.SessionStarted += async (s, e) =>
                {
                    _logger.LogInformation("Session started event.");
                };

                _recognizer.SessionStopped += (s, e) =>
                {
                    _logger.LogInformation("Session stopped event.");
                    _logger.LogInformation("Stop recognition.");
                    stopRecognition.TrySetResult(0);
                };

                // Starts continuous recognition. Uses StopContinuousRecognitionAsync() to stop recognition.
                await _recognizer.StartContinuousRecognitionAsync().ConfigureAwait(false);

                // Waits for completion.
                // Use Task.WaitAny to keep the task rooted.
                Task.WaitAny(new[] { stopRecognition.Task });

                // Stops recognition.
                await _recognizer.StopContinuousRecognitionAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException ex)
            {
                _logger.LogError(ex, "The queue processing task object has been disposed.");
            }
            catch (Exception ex)
            {
                // Catch all other exceptions and log
                _logger.LogError(ex, "Caught Exception");
            }

            _isDraining = false;
        }

        private async Task TextToSpeech(IReadOnlyDictionary<string, string> captions)
        {
            // convert the text to speech
            SpeechSynthesisResult result = await _synthesizer.SpeakTextAsync(captions["en"]);
            // take the stream of the result
            // create 20ms media buffers of the stream
            // and send to the AudioSocket in the BotMediaStream
            using (var stream = AudioDataStream.FromResult(result))
            {
                var currentTick = DateTime.Now.Ticks;
                MediaStreamEventArgs args = new MediaStreamEventArgs
                {
                    AudioMediaBuffers = Util.Utilities.CreateAudioMediaBuffers(stream, currentTick, _logger)
                };
                OnSendMediaBufferEventArgs(this, args);
            }
        }

        private async Task Transcript(IReadOnlyDictionary<string, string> captions, ulong offset, TimeSpan duration, string sourceLang, string sourceText)
        {
            long startMs = (long)(offset / 10_000UL); // 1ms = 10,000 ticks
            long endMs = startMs + (long)duration.TotalMilliseconds;

            // determine speaker label from active speakers if available
            string speakerLabel = "Bot";
            if (_activeSpeakers is { Length: > 0 })
            {
                var audioId = _activeSpeakers[0].ToString();
                if (!_audioToIdentityMap.TryGetValue(audioId, out speakerLabel))
                    speakerLabel = $"Speaker-{_activeSpeakers[0]}";
            }

            var payload = new CaptionPayload(
                Type: "caption",
                MeetingId: _threadId,
                Speaker: speakerLabel,
                SourceLang: sourceLang,
                Text: BuildTextDictionary(captions, sourceLang, sourceText),
                IsFinal: true,
                StartMs: startMs,
                EndMs: endMs
            );

            // send the transcript to the websocket clients
            await _wsPublisher.PublishCaptionAsync(payload);

            var listKey = $"list:{_threadId}";
            await _mux.GetDatabase().ListRightPushAsync(listKey, JsonConvert.SerializeObject(payload));
            await _mux.GetDatabase().KeyExpireAsync(listKey, TimeSpan.FromHours(1));
        }

        private Dictionary<string, string> BuildTextDictionary(IReadOnlyDictionary<string, string> captions, string sourceLang, string sourceText)
        {
            var dict = captions.ToDictionary(k => k.Key, v => v.Value);
            // 注意：原文语言可能就是 zh-CN 或 en-US，看你的识别输出
            dict[sourceLang] = sourceText;
            return dict;
        }
    }
}
