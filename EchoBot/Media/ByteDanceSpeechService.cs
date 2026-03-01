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

        public ByteDanceSpeechService()
        {
            _byteDanceSettings = ServiceLocator.GetRequiredService<IOptions<ByteDanceSettings>>().Value;
        }

        public override async Task AppendAudioBuffer(AudioMediaBuffer audioBuffer, string speakerId)
        {
            // If not running, ensure only the first caller triggers Start().
            if (!IsRunning)
            {
                // If another caller already set _starting to 1, drop this call.
                if (System.Threading.Interlocked.CompareExchange(ref _starting, 1, 0) != 0)
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

                    //if (!SetCurrentSpeaker(speakerId, buffer, bufferLength))
                    //    return;
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
                // Report the source audio format as PCM so the remote service treats the binary
                // frames as raw PCM samples.
                SourceAudio = new Audio { Format = "pcm", Rate = 16000, Bits = 16, Channel = 1 },
                TargetAudio = new Audio { Format = "ogg_opus", Rate = 48000 },
                Request = new Data.Speech.Ast.ReqParams { Mode = "s2s", SourceLanguage = "zh", TargetLanguage = "en" }
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
            var sourceText = new StringBuilder();
            var targetText = new StringBuilder();
            var buffer = new byte[64 * 1024];

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
                            Logger.LogInformation("Session failed: {StatusCode} {Message}", resp.ResponseMeta?.StatusCode, resp.ResponseMeta?.Message);
                            _sessionEnded.TrySetResult(true);
                        }
                        else if (eventType == EV.Type.SessionCanceled)
                        {
                            Logger.LogInformation("Session canceled");
                            _sessionEnded.TrySetResult(true);
                        }
                        else if (eventType == EV.Type.SessionFinished)
                        {
                            Logger.LogInformation("Session finished");
                            if (_recvAudio.Length > 0)
                            {
                                var audioPath = Path.Combine(".", $"v4_translate_audio_{sessionId}.opus");
                                await File.WriteAllBytesAsync(audioPath, _recvAudio.ToArray());
                                Logger.LogInformation("Saved audio: " + audioPath);
                                Logger.LogInformation("Source Text: " + sourceText.ToString());
                                Logger.LogInformation("Target Text: " + targetText.ToString());
                            }
                            else
                            {
                                Logger.LogWarning("Session finished, no audio received.");
                            }
                            _sessionEnded.TrySetResult(true);
                        }
                        else
                        {
                            // Regular data/partial
                            if (resp.Data != null)
                            {
                                await _recvAudio.WriteAsync(resp.Data.ToByteArray());
                            }
                            if (!string.IsNullOrEmpty(resp.Text) && resp.Event == EV.Type.SourceSubtitleResponse)
                                sourceText.Append(resp.Text);
                            if (!string.IsNullOrEmpty(resp.Text) && resp.Event == EV.Type.TranslationSubtitleResponse)
                                targetText.Append(resp.Text);
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
