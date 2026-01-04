using EchoBot.Util;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.Translation;
using Microsoft.Skype.Bots.Media;
using Newtonsoft.Json;
using StackExchange.Redis;
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
        public SpeechService(AppSettings settings, ILogger logger, string threadId = "")
        {
            _logger = logger;
            _speechConfig = SpeechTranslationConfig.FromSubscription(settings.SpeechConfigKey, settings.SpeechConfigRegion);
            _appSettings = settings;
            _threadId = threadId ?? string.Empty;

            // 添加目标语言
            settings.TargetLanguages.ForEach(lang => _speechConfig.AddTargetLanguage(lang));
            // 提升识别准确率
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

                        // 提供要“自动检测”的候选语言数组（最多 ~10 个，建议 2-3 个常见语言）
                        var autoDetect = AutoDetectSourceLanguageConfig.FromLanguages(_appSettings.SourceLanguages);
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
                        if (string.IsNullOrEmpty(e.Result.Text))
                            return;

                        _logger.LogInformation($"RECOGNIZED: Text={e.Result.Text}");

                        var original = e.Result.Text;                  // 原文
                        var translated = e.Result.Translations["en"];  // 翻译文本

                        // We recognized the speech
                        // Now do Speech to Text
                        await TextToSpeech(translated);
                        await Transcript(translated);
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
                    _logger.LogInformation("\nSession started event.");
                    await TextToSpeech("Hello");
                };

                _recognizer.SessionStopped += (s, e) =>
                {
                    _logger.LogInformation("\nSession stopped event.");
                    _logger.LogInformation("\nStop recognition.");
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

        private async Task TextToSpeech(string text)
        {
            // convert the text to speech
            SpeechSynthesisResult result = await _synthesizer.SpeakTextAsync(text);
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

        private async Task Transcript(string text)
        {
            // determine speaker label from active speakers if available
            string speakerLabel = "Bot";
            if (_activeSpeakers is { Length: > 0 })
                speakerLabel = $"speaker-{_activeSpeakers[0]}";

            var payload = new CaptionPayload(
                Type: "caption",
                MeetingId: _threadId,
                Speaker: speakerLabel,
                Lang: "en",
                TargetLang: "zh",
                Text: text,
                IsFinal: false,
                StartMs: 1000,
                EndMs: 1500
            );

            // send the transcript to the websocket clients
            await _wsPublisher.PublishCaptionAsync(payload);

            var listKey = $"list:{_threadId}";
            await _mux.GetDatabase().ListRightPushAsync(listKey, JsonConvert.SerializeObject(payload));
            await _mux.GetDatabase().KeyExpireAsync(listKey, TimeSpan.FromHours(1));
        }
    }
}
