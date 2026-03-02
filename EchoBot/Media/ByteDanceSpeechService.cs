using Data.Speech.Ast;
using Data.Speech.Common;
using Data.Speech.Understanding;
using EchoBot.Models.Configuration;
using EchoBot.Util;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using Microsoft.Skype.Bots.Media;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using EV = Data.Speech.Event;

namespace EchoBot.Media
{
    public class ByteDanceSpeechService : BaseSpeechService
    {
        private readonly ByteDanceSettings _byteDanceSettings;

        private ClientWebSocket _wsClient;

        private string _sessionId;

        private TaskCompletionSource<bool> _sessionEnded;

        private TaskCompletionSource<bool> _handshakeDone;

        private MemoryStream _recvAudio;

        private const int ChunkSize = 3200;

        private const string UID = "ast_csharp_client";

        // 0 = not starting, 1 = starting/in-progress
        private int _starting = 0;

        private readonly string _sourceLang = "zh-CN";

        private readonly Dictionary<string, string> _translateTarget = new()
        {
            {"zh-CN","en" },
            {"en-US","zh" }
        };

        public ByteDanceSpeechService(string threadId) : base(threadId)
        {
            _byteDanceSettings = ServiceLocator.GetRequiredService<IOptions<ByteDanceSettings>>().Value;
        }

        public override async Task AppendAudioBuffer(AudioMediaBuffer audioBuffer, string speakerId)
        {
            // If not running, ensure only the first caller triggers Start().
            if (!IsRunning)
            {
                // If another caller already set _starting to 1, drop this call.
                if (Interlocked.CompareExchange(ref _starting, 1, 0) != 0)
                    return;

                await Start();
            }

            try
            {
                var bufferLength = audioBuffer.Length;
                if (bufferLength > 0)
                {
                    var buffer = new byte[bufferLength];
                    Marshal.Copy(audioBuffer.Data, buffer, 0, (int)bufferLength);

                    SetCurrentSpeaker(speakerId, buffer, bufferLength);

                    var total = (int)bufferLength;
                    var offset = 0;
                    while (offset < total && _wsClient.State == WebSocketState.Open && !_sessionEnded.Task.IsCompleted)
                    {
                        var toSend = Math.Min(ChunkSize, total - offset);
                        var chunkReq = new TranslateRequest
                        {
                            RequestMeta = new RequestMeta { SessionID = _sessionId },
                            Event = EV.Type.TaskRequest,
                            SourceAudio = new Audio { BinaryData = ByteString.CopyFrom(buffer, offset, toSend) }
                        };
                        await _wsClient.SendAsync(new ArraySegment<byte>(chunkReq.ToByteArray()), WebSocketMessageType.Binary, true, CancellationToken.None);
                        offset += toSend;
                        await Task.Delay(10);
                    }
                }
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Send error");
                _sessionEnded.TrySetResult(true);
            }

            await _sessionEnded.Task.ConfigureAwait(false);

            if (_wsClient.State == WebSocketState.Open)
                await _wsClient.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session completed", CancellationToken.None);
        }

        private async Task Start()
        {
            _recvAudio = new MemoryStream();
            _handshakeDone = new TaskCompletionSource<bool>();
            _sessionEnded = new TaskCompletionSource<bool>();

            _wsClient = await CreateWsClient();

            _sessionId = Guid.NewGuid().ToString();
            // Start receive loop
            _ = Task.Run(async () => await ReceiveMessage(_sessionId));

            await StartSession(_sessionId);

            // Wait handshake
            await _handshakeDone.Task.ConfigureAwait(false);

            IsRunning = true;
        }

        private async Task<ClientWebSocket> CreateWsClient()
        {
            var wsClient = new ClientWebSocket();
            wsClient.Options.SetRequestHeader("X-Api-App-Key", _byteDanceSettings.AppKey);
            wsClient.Options.SetRequestHeader("X-Api-Access-Key", _byteDanceSettings.AccessKey);
            wsClient.Options.SetRequestHeader("X-Api-Resource-Id", _byteDanceSettings.ResourceId);
            wsClient.Options.SetRequestHeader("X-Api-Connect-Id", Guid.NewGuid().ToString());

            var wsUrl = new Uri(new Uri(_byteDanceSettings.Host + "/"), _byteDanceSettings.Endpoint);
            await wsClient.ConnectAsync(wsUrl, CancellationToken.None);

            Logger.LogInformation("WebSocket connected");

            return wsClient;
        }

        private async Task StartSession(string sessionId)
        {
            // Send StartSession
            var startReq = new TranslateRequest
            {
                RequestMeta = new RequestMeta { SessionID = sessionId },
                Event = EV.Type.StartSession,
                User = new User { Uid = UID, Did = UID },
                // Teams sends raw PCM 16-bit little-endian samples (not a WAV file with RIFF header).
                // Report the source audio format as PCM so the remote service treats the binary frames as raw PCM samples.
                SourceAudio = new Audio { Format = "pcm", Rate = 16000, Bits = 16, Channel = 1 },
                TargetAudio = new Audio { Format = "ogg_opus", Rate = 48000 },
                Request = new Data.Speech.Ast.ReqParams { Mode = "s2s", SourceLanguage = _sourceLang.Split('-')[0], TargetLanguage = _translateTarget[_sourceLang] }
            };
            await _wsClient.SendAsync(new ArraySegment<byte>(startReq.ToByteArray()), WebSocketMessageType.Binary, true, CancellationToken.None);
            Logger.LogInformation("StartSession sent");
        }

        public async Task FinishSession(string sessionId)
        {
            if (!_sessionEnded.Task.IsCompleted && _wsClient.State == WebSocketState.Open)
            {
                var finishReq = new TranslateRequest
                {
                    RequestMeta = new RequestMeta { SessionID = sessionId },
                    Event = EV.Type.FinishSession
                };
                await _wsClient.SendAsync(new ArraySegment<byte>(finishReq.ToByteArray()), WebSocketMessageType.Binary, true, CancellationToken.None);

                _sessionEnded.TrySetResult(true);
                Logger.LogInformation("FinishSession sent");
            }
        }

        private async Task ReceiveMessage(string sessionId)
        {
            var buffer = new byte[64 * 1024];
            var sourceText = new StringBuilder();
            var tranlatedText = new StringBuilder();

            try
            {
                while (_wsClient.State == WebSocketState.Open)
                {
                    var ms = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _wsClient.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await _wsClient.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None);
                            _sessionEnded.TrySetResult(true);
                            return;
                        }
                        ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    var data = ms.ToArray();
                    try
                    {
                        var resp = TranslateResponse.Parser.ParseFrom(data);
                        var eventType = resp.Event;

                        if (eventType == EV.Type.SessionStarted)
                        {
                            _handshakeDone.TrySetResult(true);
                            Logger.LogInformation("Session (ID={sessionId}) started.", sessionId);
                        }
                        else if (eventType == EV.Type.SessionFailed)
                        {
                            Logger.LogWarning("Session failed: {StatusCode} {Message}", resp.ResponseMeta?.StatusCode, resp.ResponseMeta?.Message);
                            _sessionEnded.TrySetResult(true);
                        }
                        else if (eventType == EV.Type.SessionCanceled)
                        {
                            Logger.LogWarning("Session canceled");
                            _sessionEnded.TrySetResult(true);
                        }
                        else if (eventType == EV.Type.SessionFinished)
                        {
                            Logger.LogInformation("Session finished");
                            _sessionEnded.TrySetResult(true);
                        }
                        else if (eventType == EV.Type.TranslationSubtitleEnd)
                        {
                            // Recognized，断句发生，可以在这里处理字幕显示逻辑，比如把sourceText和targetText发送到前端显示，然后清空StringBuilder准备下一句的字幕
                            var original = sourceText.ToString();
                            var transleted = tranlatedText.ToString();

                            if (string.IsNullOrEmpty(original)) return;

                            Logger.LogDebug("RECOGNIZED in {sourceLang}: Text={original}", _sourceLang, original);
                            await BatchTranslateAsync(original, _sourceLang, (ulong)resp.StartTime, TimeSpan.FromSeconds(30), CurrentSpeakerId);

                            sourceText.Clear();
                            tranlatedText.Clear();
                        }
                        else
                        {
                            // Regular data/partial (Recognizing)
                            var speakerId = CurrentSpeakerId;
                            if (!string.IsNullOrEmpty(resp.Text) && new List<EV.Type> { EV.Type.SourceSubtitleResponse, EV.Type.TranslationSubtitleResponse }.Contains(resp.Event))
                            {
                                (resp.Event == EV.Type.SourceSubtitleResponse ? sourceText : tranlatedText).Append(resp.Text);
                            }

                            if (resp.Event == EV.Type.TranslationSubtitleResponse)
                            {
                                var partialText = sourceText.ToString();
                                var translatedText = tranlatedText.ToString();
                                var captions = BuildTextDictionary(new Dictionary<string, string> { { _sourceLang, partialText }, { _translateTarget[_sourceLang], translatedText } },
                                    _sourceLang, partialText);

                                Logger.LogDebug("RECOGNIZING in {sourceLang}: Text={Text}", _sourceLang, partialText);

                                _ = Transcript(captions, false, (ulong)resp.StartTime, TimeSpan.FromSeconds(30), _sourceLang, partialText, speakerId);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Parse error");
                    }
                }
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Receiver error");
                _sessionEnded.TrySetResult(true);
            }
        }

        public override void AddPhrases(IEnumerable<string> phrases)
        {
            foreach (var p in phrases)
            {
                if (!string.IsNullOrWhiteSpace(p))
                    Logger.LogInformation(p);
            }
        }

        public override async Task ShutDownAsync()
        {
            await FinishSession(_sessionId);
        }
    }
}
