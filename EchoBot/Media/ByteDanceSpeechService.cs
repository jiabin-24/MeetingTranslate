using Data.Speech.Ast;
using Data.Speech.Common;
using Data.Speech.Understanding;
using EchoBot.Models.Configuration;
using EchoBot.Util;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using Microsoft.Skype.Bots.Media;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using EV = Data.Speech.Event;

namespace EchoBot.Media
{
    public class ByteDanceSpeechService(string threadId) : BaseSpeechService(threadId)
    {
        private readonly ByteDanceSettings _byteDanceSettings = ServiceLocator.GetRequiredService<IOptions<ByteDanceSettings>>().Value;

        private readonly ConcurrentDictionary<string, ClientWebSocket> _wsClients = [];

        private readonly ConcurrentDictionary<string, string> _sessionIds = [];

        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _handshakeDones = [];

        private readonly TaskCompletionSource<bool> _sessionEnded = new();

        private readonly byte[] _sendBuffer = new byte[ChunkSize];
        private readonly object _sendLock = new();
        private int _sendLen = 0;
        
        private const int ChunkSize = 3200;

        private const string UID = "ast_csharp_client";

        protected override string AUTO => "zhen";

        // 0 = not starting, 1 = starting/in-progress
        private int _starting = 0;

        private readonly Dictionary<string, string> _translateTarget = new()
        {
            //{"zh-CN","en" },
            //{"en-US","zh" },
            {"zhen","zhen" },
        };

        public override async Task AppendAudioBuffer(AudioMediaBuffer audioBuffer, string speakerId)
        {
            var sourceLangs = _translateTarget.Keys;

            // If not running, ensure only the first caller triggers Start().
            if (!IsRunning)
            {
                // If another caller already set _starting to 1, drop this call.
                if (Interlocked.CompareExchange(ref _starting, 1, 0) != 0)
                    return;

                await Task.WhenAll(sourceLangs.Select(Start)).ConfigureAwait(false);
                IsRunning = true;
            }

            try
            {
                var bufferLength = audioBuffer.Length;
                if (bufferLength > 0)
                {
                    var buffer = new byte[bufferLength];
                    Marshal.Copy(audioBuffer.Data, buffer, 0, (int)bufferLength);

                    SetCurrentSpeaker(speakerId, buffer, bufferLength);

                    // Append incoming 640-byte buffer and send only when we have exactly one full ChunkSize (5 * 640).
                    var chunkToSend = GetBatchBuffer(buffer, (int)bufferLength);
                    if (chunkToSend != null && _wsClients.Values.All(c => c.State == WebSocketState.Open) && !_sessionEnded.Task.IsCompleted)
                    {
                        var tasks = sourceLangs.Select(l =>
                        {
                            var chunkReq = new TranslateRequest
                            {
                                RequestMeta = new RequestMeta { SessionID = _sessionIds[l] },
                                Event = EV.Type.TaskRequest,
                                SourceAudio = new Audio { BinaryData = ByteString.CopyFrom(chunkToSend) }
                            };

                            return _wsClients[l].SendAsync(new ArraySegment<byte>(chunkReq.ToByteArray()), WebSocketMessageType.Binary, true, CancellationToken.None);
                        });
                        await Task.WhenAll(tasks).ConfigureAwait(false);
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
        }

        private async Task Start(string sourceLang)
        {
            _wsClients[sourceLang] = await CreateWsClient();
            _sessionIds[sourceLang] = Guid.NewGuid().ToString();
            _handshakeDones[sourceLang] = new TaskCompletionSource<bool>();

            // Start receive loop
            _ = ReceiveMessage(_sessionIds[sourceLang], sourceLang);
            // Send StartSession to trigger handshake
            await StartSession(_sessionIds[sourceLang], sourceLang);
            // Wait handshake
            await _handshakeDones[sourceLang].Task.ConfigureAwait(false);
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

        private async Task StartSession(string sessionId, string sourceLang)
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
                Request = new Data.Speech.Ast.ReqParams { Mode = "s2s", SourceLanguage = sourceLang.Split('-')[0], TargetLanguage = _translateTarget[sourceLang] }
            };
            await _wsClients[sourceLang].SendAsync(new ArraySegment<byte>(startReq.ToByteArray()), WebSocketMessageType.Binary, true, CancellationToken.None);
            Logger.LogInformation("StartSession sent");
        }

        public async Task FinishSession(string sessionId, string sourceLang)
        {
            var wsClient = _wsClients[sourceLang];

            if (!_sessionEnded.Task.IsCompleted && wsClient.State == WebSocketState.Open)
            {
                var finishReq = new TranslateRequest
                {
                    RequestMeta = new RequestMeta { SessionID = sessionId },
                    Event = EV.Type.FinishSession
                };
                await wsClient.SendAsync(new ArraySegment<byte>(finishReq.ToByteArray()), WebSocketMessageType.Binary, true, CancellationToken.None);

                _sessionEnded.TrySetResult(true);
                Logger.LogInformation("FinishSession sent");
            }

            if (wsClient.State == WebSocketState.Open)
                await wsClient.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session completed", CancellationToken.None);
        }

        private async Task ReceiveMessage(string sessionId, string sourceLang)
        {
            var buffer = new byte[64 * 1024];
            var sourceText = new StringBuilder();
            var tranlatedText = new StringBuilder();
            var wsClient = _wsClients[sourceLang];

            try
            {
                while (wsClient.State == WebSocketState.Open)
                {
                    var ms = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await wsClient.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await wsClient.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None);
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
                            _handshakeDones[sourceLang].TrySetResult(true);
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

                            Logger.LogDebug("RECOGNIZED in {sourceLang}: Text={original}", sourceLang, original);
                            await BatchTranslateAsync(original, sourceLang, (ulong)resp.StartTime, TimeSpan.FromSeconds(30), CurrentSpeakerId);

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

                                var partialText = sourceText.ToString();
                                var translatedText = tranlatedText.ToString();
                                var captions = BuildTextDictionary(new Dictionary<string, string> { { sourceLang, partialText } },
                                    sourceLang, partialText, translatedText);

                                Logger.LogDebug("RECOGNIZING in {sourceLang}: Text={Text}", sourceLang, partialText);

                                _ = Transcript(captions, false, (ulong)resp.StartTime, TimeSpan.FromSeconds(30), sourceLang, partialText, speakerId);
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

        private byte[] GetBatchBuffer(byte[]? buffer, int bufferLength)
        {
            // Append incoming 640-byte buffer and send only when we have exactly one full ChunkSize (5 * 640).
            byte[] chunkToSend = null;
            lock (_sendLock)
            {
                // Copy incoming data (assumed to be 640 bytes) into send buffer
                Array.Copy(buffer, 0, _sendBuffer, _sendLen, bufferLength);
                _sendLen += bufferLength;

                // When we've accumulated exactly ChunkSize, prepare chunk and reset
                if (_sendLen >= ChunkSize)
                {
                    chunkToSend = new byte[ChunkSize];
                    Array.Copy(_sendBuffer, 0, chunkToSend, 0, ChunkSize);
                    _sendLen = 0;
                }
            }
            return chunkToSend;
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
            await base.ShutDownAsync().ConfigureAwait(false);
            await Task.WhenAll(_translateTarget.Keys.Select(l => FinishSession(_sessionIds[l], l))).ConfigureAwait(false);
        }
    }
}
