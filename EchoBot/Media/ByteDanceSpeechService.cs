using Data.Speech.Ast;
using Data.Speech.Common;
using Data.Speech.Understanding;
using EchoBot.Constants;
using EchoBot.Models.Configuration;
using EchoBot.Util;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using Microsoft.Graph.Communications.Calls;
using Microsoft.Skype.Bots.Media;
using NAudio.Wave;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using EV = Data.Speech.Event;

namespace EchoBot.Media
{
    public class ByteDanceSpeechService(string threadId, List<IParticipant> participants) : BaseSpeechService(threadId, participants)
    {
        private readonly ByteDanceSettings _byteDanceSettings = ServiceLocator.GetRequiredService<IOptions<ByteDanceSettings>>().Value;

        private readonly ConcurrentDictionary<string, ClientWebSocket> _wsClients = [];

        private readonly ConcurrentDictionary<string, string> _sessionIds = [];

        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _handshakeDones = [];

        private readonly TaskCompletionSource<bool> _sessionEnded = new();

        private readonly byte[] _sendBuffer = new byte[ChunkSize];
        private readonly object _sendLock = new();
        private int _sendLen = 0;

        private readonly MemoryStream _recvAudio = new();
        private int _singleRecvTime = 0;
        private int _backlogRecvTime = 0;
        private bool _finishFlag = false;
        private int _convertLeftoverCount = 0;
        private readonly byte[] _convertLeftoverBuffer = new byte[8];

        // Send audio in 80ms chunks. At 16kHz, 16-bit mono => 32000 bytes/sec => 0.08 * 32000 = 2560 bytes
        private const int ChunkSize = 2560;

        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

        private const string UID = "ast_csharp_client";

        protected override string AUTO => "zhen";

        // 0 = not starting, 1 = starting/in-progress
        private int _starting = 0;

        private readonly Dictionary<string, string> _translateTarget = new()
        {
            //{"zh-CN","en" },
            //{"en-US","zh" },
            {"zhen","enzh" },
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
                    var converted = Utilities.ConvertToPcm16Mono16k(buffer, (int)bufferLength, new WaveFormat(16000, 16, 1), ref _convertLeftoverCount, _convertLeftoverBuffer);
                    if (converted.Length == 0)
                        return;

                    buffer = converted;
                    bufferLength = converted.Length;

                    SetCurrentSpeaker(speakerId, buffer, bufferLength);

                    // Accumulate incoming frames into a chunk buffer and only send when we have exactly one full ChunkSize (80ms)
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

                            return _wsClients[l].SendAsync(new ArraySegment<byte>(chunkReq.ToByteArray()), WebSocketMessageType.Binary, true, new CancellationTokenSource(DefaultTimeout).Token);
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
            await wsClient.ConnectAsync(wsUrl, new CancellationTokenSource(DefaultTimeout).Token);

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
                TargetAudio = new Audio { Format = "pcm", Rate = 16000 },
                Request = new Data.Speech.Ast.ReqParams { Mode = "s2s", SourceLanguage = sourceLang.Split('-')[0], TargetLanguage = sourceLang.Equals(AUTO) ? AUTO : _translateTarget[sourceLang] }
            };
            await _wsClients[sourceLang].SendAsync(new ArraySegment<byte>(startReq.ToByteArray()), WebSocketMessageType.Binary, true, new CancellationTokenSource(DefaultTimeout).Token);
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
                await wsClient.SendAsync(new ArraySegment<byte>(finishReq.ToByteArray()), WebSocketMessageType.Binary, true, new CancellationTokenSource(DefaultTimeout).Token);

                _sessionEnded.TrySetResult(true);
                Logger.LogInformation("FinishSession sent");
            }

            if (wsClient.State == WebSocketState.Open)
                await wsClient.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session completed", new CancellationTokenSource(DefaultTimeout).Token);
        }

        private async Task ReceiveMessage(string sessionId, string sourceLang)
        {
            var buffer = new byte[64 * 1024];
            var sourceSb = new StringBuilder();
            var tranlatedSb = new StringBuilder();
            var wsClient = _wsClients[sourceLang];
            var receiveTimeout = TimeSpan.FromSeconds(10);

            try
            {
                while (wsClient.State == WebSocketState.Open)
                {
                    var ms = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        // Use a short timeout for each ReceiveAsync. If no data arrives within the timeout, reconnect.
                        try
                        {
                            using var cts = new CancellationTokenSource(receiveTimeout);
                            result = await wsClient.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            Logger.LogWarning("Receive timeout {ReceiveTimeout}s for session {sessionId}, sourceLang {sourceLang}. Reconnecting...",
                                receiveTimeout.TotalSeconds, sessionId, sourceLang);

                            await Reconnect(wsClient, sourceLang);
                            return;
                        }
                        if (result.MessageType == WebSocketMessageType.Close && wsClient.State == WebSocketState.Open)
                        {
                            await wsClient.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", new CancellationTokenSource(DefaultTimeout).Token);
                            _sessionEnded.TrySetResult(true);
                            return;
                        }
                        ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage && wsClient.State == WebSocketState.Open);

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
                        else if (eventType == EV.Type.TtssentenceEnd)
                        {
                            _singleRecvTime = resp.StartTime;

                            // Recognized，断句发生，可以在这里处理字幕显示逻辑，比如把sourceText和targetText发送到前端显示，然后清空StringBuilder准备下一句的字幕
                            var original = sourceSb.ToString();
                            if (string.IsNullOrEmpty(original))
                            {
                                sourceSb.Clear();
                                tranlatedSb.Clear();
                                continue;
                            }

                            var translatedText = tranlatedSb.ToString();
                            var configTarLang = _translateTarget[sourceLang];
                            // 火山引擎使用AUTO（zhen）时不会将识别的语言返回，所以需要自己检测是中文还是英文，以决定翻译的目标语言
                            var detectTarLang = AUTO.Equals(sourceLang) ? AppConstants.LangCode(LangDetect.DetectZhEn(translatedText)) : configTarLang;
                            var transleteDic = new Dictionary<string, string> { { detectTarLang, translatedText } };

                            Logger.LogDebug("RECOGNIZED in {sourceLang}: Text={original}", sourceLang, original);
                            await BatchTranslateAsync(original, sourceLang, (ulong)(resp.StartTime + _backlogRecvTime), TimeSpan.FromSeconds(30), CurrentSpeakerId, transleteDic);

                            if (_recvAudio.Length > 0)
                            {
                                //var pcm16 = ResamplePcm16(_recvAudio.ToArray(), 24000, 16000);
                                await TextToSpeech(_recvAudio.ToArray(), detectTarLang, sourceLang, CurrentSpeakerId);

                                _recvAudio.SetLength(0); // 清空缓冲
                                _recvAudio.Position = 0;
                            }

                            sourceSb.Clear();
                            tranlatedSb.Clear();
                        }
                        else
                        {
                            // Regular data/partial (Recognizing)
                            var speakerId = CurrentSpeakerId;
                            if (resp.Data != null)
                            {
                                await _recvAudio.WriteAsync(resp.Data.ToByteArray());
                            }
                            if (!string.IsNullOrWhiteSpace(resp.Text) && new List<EV.Type> { EV.Type.SourceSubtitleResponse, EV.Type.TranslationSubtitleResponse }.Contains(resp.Event))
                            {
                                (resp.Event == EV.Type.SourceSubtitleResponse ? sourceSb : tranlatedSb).Append(resp.Text);

                                var partialText = sourceSb.ToString();
                                var translatedText = tranlatedSb.ToString();

                                var partialLang = AppConstants.LangCode(LangDetect.DetectZhEn(partialText));
                                var translatedLang = AppConstants.LangCode(LangDetect.DetectZhEn(translatedText));
                                var langDic = new Dictionary<string, string> { { sourceLang, partialText } };
                                langDic[partialLang] = partialText;
                                langDic[translatedLang] = translatedText;

                                var captions = BuildTextDictionary(langDic, sourceLang, partialText);

                                Logger.LogDebug("RECOGNIZING in {sourceLang}: Text={Text}", sourceLang, partialText);

                                await Transcript(captions, false, (ulong)(_singleRecvTime + _backlogRecvTime), TimeSpan.FromSeconds(30), sourceLang, partialText, speakerId);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Parse error");
                    }
                }

                Logger.LogWarning("Session {sessionId} ended", sessionId);
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Receiver error");
                _sessionEnded.TrySetResult(true);
            }
        }

        private async Task Reconnect(ClientWebSocket? wsClient, string sourceLang)
        {
            // Attempt to close the current socket and establish a new one, then exit this receive loop.
            try
            {
                if (wsClient.State == WebSocketState.Open || wsClient.State == WebSocketState.CloseReceived || wsClient.State == WebSocketState.CloseSent)
                {
                    await wsClient.CloseAsync(WebSocketCloseStatus.NormalClosure, "reconnect", new CancellationTokenSource(DefaultTimeout).Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Error while closing stale websocket before reconnect");
            }

            try
            {
                await Task.Delay(1000).ConfigureAwait(false); // Wait a moment before reconnecting to avoid tight reconnect loops
                // Create a new websocket and replace the dictionary entry
                var sessionId = Guid.NewGuid().ToString();
                var newClient = await CreateWsClient().ConfigureAwait(false);
                _wsClients[sourceLang] = newClient;
                _sessionIds[sourceLang] = sessionId;

                // Replace handshake tcs before StartSession so SessionStarted can complete the correct task
                var handshakeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _handshakeDones[sourceLang] = handshakeTcs;

                // Start new receive loop and re-send StartSession to re-handshake
                _ = ReceiveMessage(sessionId, sourceLang);
                await StartSession(sessionId, sourceLang).ConfigureAwait(false);
                // Wait handshake
                await handshakeTcs.Task.ConfigureAwait(false);

                _backlogRecvTime += _singleRecvTime; // Add time of last received message to backlog time, so translated captions don't jump back in time after reconnect
                _singleRecvTime = 0;

                if (_finishFlag)
                    await FinishSession(sessionId, sourceLang);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Reconnect failed for session {sessionId} sourceLang {sourceLang}", _sessionIds[sourceLang], sourceLang);
                _sessionEnded.TrySetResult(true);
            }
        }

        protected override async Task TextToSpeech(string text, string lang, string sourceLang, string speakerId)
        {
            await Task.CompletedTask;
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
            _finishFlag = true;
        }
    }
}
