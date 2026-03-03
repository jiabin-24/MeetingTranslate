using EchoBot.Constants;
using EchoBot.Util;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.Translation;
using Microsoft.Extensions.Options;
using Microsoft.Graph.Communications.Common;
using Microsoft.Skype.Bots.Media;
using Sprache;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Data;
using System.Runtime.InteropServices;

namespace EchoBot.Media
{
    /// <summary>
    /// Class AzureSpeechService.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="AzureSpeechService" /> class.
    /// </remarks>
    public class AzureSpeechService(string threadId) : BaseSpeechService(threadId)
    {
        /// <summary>
        /// The is draining indicator
        /// </summary>
        protected bool _isDraining;

        protected override string AUTO => "auto";

        private readonly AppSettings _speechSettings = ServiceLocator.GetRequiredService<IOptions<AppSettings>>().Value;

        private readonly AudioOutputStream _audioOutputStream = AudioOutputStream.CreatePullStream();

        // per-speaker streams and recognizers
        private readonly PushAudioInputStream _audioInputStream = AudioInputStream.CreatePushStream(AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1));
        private readonly ConcurrentDictionary<string, PushAudioInputStream> _audioInputStreamDic = [];
        private readonly Dictionary<string, TranslationRecognizer> _recognizerDic = [];
        private PhraseListGrammar _phraseList;

        private async Task CreateRecognizer(string sourceLang)
        {
            var audioInputStream = AudioInputStream.CreatePushStream(AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1));
            var speechConfig = SpeechConfig(sourceLang);
            if (_speechSettings.CustomSpeechEndpoints.TryGetValue(sourceLang, out string endpoint) && !string.IsNullOrEmpty(endpoint))
                speechConfig.EndpointId = endpoint;

            TranslationRecognizer recognizer;
            if (!sourceLang.Equals(AUTO))
                recognizer = new TranslationRecognizer(speechConfig, AudioConfig.FromStreamInput(audioInputStream));
            else
            {
                // prepare auto-detect config for recognizers
                var speechEndpoints = _speechSettings.CustomSpeechEndpoints.ToDictionary(k => k.Key, endpoint => endpoint.Value == null ? SourceLanguageConfig.FromLanguage(endpoint.Key)
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
            var speechConfig = SpeechTranslationConfig.FromSubscription(_speechSettings.SpeechConfigKey, _speechSettings.SpeechConfigRegion);
            if (!sourceLang.Equals(AUTO))
                speechConfig.SpeechRecognitionLanguage = sourceLang;

            TranslatorOptions.Routing.Keys.ForEach(lang => speechConfig.AddTargetLanguage(lang.Split('-')[0]));

            // 提升识别准确率
            //speechConfig.SetProperty(PropertyId.SpeechServiceConnection_LanguageIdMode, "Continuous"); // 持续检测语言
            speechConfig.SetProperty(PropertyId.Speech_SegmentationSilenceTimeoutMs, "200"); // 让断句更短
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
            var sourceLangs = _speechSettings.CustomSpeechEndpoints.Keys;

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
    }
}
