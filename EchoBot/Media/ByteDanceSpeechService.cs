using Data.Speech.Ast;
using Data.Speech.Common;
using Data.Speech.Understanding;
using EchoBot.Bot;
using EchoBot.Constants;
using EchoBot.Models.Configuration;
using EchoBot.Util;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using Microsoft.Graph.Communications.Common;
using Microsoft.Skype.Bots.Media;
using NAudio.Wave;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using EV = Data.Speech.Event;

namespace EchoBot.Media
{
    public class ByteDanceSpeechService(string threadId, CallHandler callHandler) : BaseSpeechService(threadId, callHandler)
    {
        private readonly ByteDanceSettings _byteDanceSettings = ServiceLocator.GetRequiredService<IOptions<ByteDanceSettings>>().Value;

        private readonly ConcurrentDictionary<string, SpeakerSession> _speakerSessions = [];

        // Send audio in 80ms chunks. At 16kHz, 16-bit mono => 32000 bytes/sec => 0.08 * 32000 = 2560 bytes
        private const int ChunkSize = 2560;

        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan SilenceSendInterval = TimeSpan.FromMilliseconds(80);
        private static readonly TimeSpan SilenceStartAfterNoAudio = TimeSpan.FromMilliseconds(120);

        private const string UID = "ast_csharp_client";
        private HashSet<string> _hotWords = [];

        protected override string AUTO => "zhen";

        private sealed class SpeakerSession
        {
            public string SpeakerId { get; init; } = string.Empty;
            public ConcurrentDictionary<string, ClientWebSocket> WsClients { get; } = [];
            public ConcurrentDictionary<string, string> SessionIds { get; } = [];
            public ConcurrentDictionary<string, TaskCompletionSource<bool>> HandshakeDones { get; } = [];
            public TaskCompletionSource<bool> SessionEnded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public byte[] SendBuffer { get; } = new byte[ChunkSize];
            public object SendLock { get; } = new();
            public int SendLen;
            public MemoryStream RecvAudio { get; } = new();
            public ulong TranslatingTime;
            public bool FinishFlag;
            public int ConvertLeftoverCount;
            public byte[] ConvertLeftoverBuffer { get; } = new byte[8];
            public int Starting;
            public int Running;
            public long LastAudioTicks;
            public CancellationTokenSource KeepAliveCts { get; } = new();
            public Task? KeepAliveTask;
        }

        private readonly Dictionary<string, string> _translateTarget = new()
        {
            //{"zh-CN","en" },
            //{"en-US","zh" },
            {"zhen","enzh" },
        };

        public override async Task AppendAudioBuffer(AudioMediaBuffer audioBuffer, string speakerId)
        {
            if (string.IsNullOrWhiteSpace(speakerId))
                speakerId = "unknown";

            var sourceLangs = _translateTarget.Keys;
            var session = _speakerSessions.GetOrAdd(speakerId, id => new SpeakerSession { SpeakerId = id });

            // Per-speaker lazy start: ensure only first caller creates recognizers for this speaker.
            if (Volatile.Read(ref session.Running) == 0)
            {
                if (Interlocked.CompareExchange(ref session.Starting, 1, 0) != 0)
                    return;

                await Task.WhenAll(sourceLangs.Select(lang => Start(session, lang, speakerId))).ConfigureAwait(false);
                Volatile.Write(ref session.Running, 1);
                StartKeepAliveLoop(session);
                IsRunning = true;
            }

            try
            {
                var bufferLength = audioBuffer.Length;
                if (bufferLength > 0)
                {
                    var buffer = new byte[bufferLength];
                    Marshal.Copy(audioBuffer.Data, buffer, 0, (int)bufferLength);
                    var converted = Util.Utilities.ConvertToPcm16Mono16k(buffer, (int)bufferLength, new WaveFormat(16000, 16, 1), ref session.ConvertLeftoverCount, session.ConvertLeftoverBuffer);
                    if (converted.Length == 0)
                        return;

                    buffer = converted;
                    bufferLength = converted.Length;
                    Volatile.Write(ref session.LastAudioTicks, DateTime.UtcNow.Ticks);

                    // Accumulate incoming frames into a chunk buffer and only send when we have exactly one full ChunkSize (80ms)
                    var chunkToSend = GetBatchBuffer(session, buffer, (int)bufferLength);
                    if (chunkToSend != null && session.WsClients.Values.All(c => c.State == WebSocketState.Open) && !session.SessionEnded.Task.IsCompleted)
                    {
                        await SendChunkAsync(session, chunkToSend).ConfigureAwait(false);
                        await Task.Delay(10);
                    }
                }
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Send error");
                session.SessionEnded.TrySetResult(true);
            }
        }

        private async Task Start(SpeakerSession session, string sourceLang, string speakerId)
        {
            Volatile.Write(ref session.LastAudioTicks, DateTime.UtcNow.Ticks);
            session.WsClients[sourceLang] = await CreateWsClient();
            session.SessionIds[sourceLang] = Guid.NewGuid().ToString();
            session.HandshakeDones[sourceLang] = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Start receive loop
            _ = ReceiveMessage(session, session.SessionIds[sourceLang], sourceLang, speakerId);
            // Send StartSession to trigger handshake
            await StartSession(session, session.SessionIds[sourceLang], sourceLang);
            // Wait handshake
            await session.HandshakeDones[sourceLang].Task.ConfigureAwait(false);
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

        private async Task StartSession(SpeakerSession session, string sessionId, string sourceLang)
        {
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
            await session.WsClients[sourceLang].SendAsync(new ArraySegment<byte>(startReq.ToByteArray()), WebSocketMessageType.Binary, true, new CancellationTokenSource(DefaultTimeout).Token);
            await CacheHelper.SetAsync(CacheConstants.CallParticipantsKey(ThreadId, GetParticipant(session.SpeakerId).DisplayName), TimeSpan.FromMinutes(30), "exist").ConfigureAwait(false);

            Logger.LogInformation("StartSession sent");
        }

        private async Task UpdateSession(SpeakerSession session, string sourceLang)
        {
            if (!session.WsClients.TryGetValue(sourceLang, out var wsClient) || !session.SessionIds.TryGetValue(sourceLang, out var sessionId))
                return;

            if (!session.SessionEnded.Task.IsCompleted && wsClient.State == WebSocketState.Open)
            {
                var req = new Data.Speech.Ast.ReqParams { Corpus = new Corpus() };
                req.Corpus.HotWordsList.Clear();
                req.Corpus.HotWordsList.AddRange(_hotWords.Where(w => !string.IsNullOrWhiteSpace(w)));

                var updateReq = new TranslateRequest
                {
                    RequestMeta = new RequestMeta { SessionID = sessionId },
                    Event = EV.Type.UpdateConfig,
                    Request = req
                };
                await wsClient.SendAsync(new ArraySegment<byte>(updateReq.ToByteArray()), WebSocketMessageType.Binary, true, new CancellationTokenSource(DefaultTimeout).Token);
                Logger.LogInformation("UpdateConfig sent with {HotWordCount} hot words", req.Corpus.HotWordsList.Count);
            }
        }

        private async Task FinishSession(SpeakerSession session, string sourceLang)
        {
            if (!session.WsClients.TryGetValue(sourceLang, out var wsClient) || !session.SessionIds.TryGetValue(sourceLang, out var sessionId))
                return;

            if (!session.SessionEnded.Task.IsCompleted && wsClient.State == WebSocketState.Open)
            {
                var finishReq = new TranslateRequest
                {
                    RequestMeta = new RequestMeta { SessionID = sessionId },
                    Event = EV.Type.FinishSession
                };
                await wsClient.SendAsync(new ArraySegment<byte>(finishReq.ToByteArray()), WebSocketMessageType.Binary, true, new CancellationTokenSource(DefaultTimeout).Token);
                await CacheHelper.DeleteAsync(CacheConstants.CallParticipantsKey(ThreadId, GetParticipant(session.SpeakerId).DisplayName)).ConfigureAwait(false);

                session.SessionEnded.TrySetResult(true);
                Logger.LogInformation("FinishSession sent for speaker {speakerId}", session.SpeakerId);
            }

            if (wsClient.State == WebSocketState.Open)
                await wsClient.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session completed", new CancellationTokenSource(DefaultTimeout).Token);
        }

        private async Task ReceiveMessage(SpeakerSession session, string sessionId, string sourceLang, string speakerId)
        {
            var buffer = new byte[64 * 1024];
            var sourceSb = new StringBuilder();
            var tranlatedSb = new StringBuilder();
            var wsClient = session.WsClients[sourceLang];
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

                            await Reconnect(session, wsClient, sourceLang, speakerId);
                            return;
                        }
                        if (result.MessageType == WebSocketMessageType.Close && wsClient.State == WebSocketState.Open)
                        {
                            await wsClient.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", new CancellationTokenSource(DefaultTimeout).Token);
                            session.SessionEnded.TrySetResult(true);
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
                            session.HandshakeDones[sourceLang].TrySetResult(true);
                            Logger.LogInformation("Session (ID={sessionId}) started.", sessionId);
                        }
                        else if (eventType == EV.Type.SessionFailed)
                        {
                            Logger.LogWarning("Session failed: {StatusCode} {Message}", resp.ResponseMeta?.StatusCode, resp.ResponseMeta?.Message);
                            session.SessionEnded.TrySetResult(true);
                        }
                        else if (eventType == EV.Type.SessionCanceled)
                        {
                            Logger.LogWarning("Session canceled");
                            session.SessionEnded.TrySetResult(true);
                        }
                        else if (eventType == EV.Type.SessionFinished)
                        {
                            Logger.LogInformation("Session finished");
                            session.SessionEnded.TrySetResult(true);
                        }
                        else if (eventType == EV.Type.TtssentenceEnd)
                        {
                            // Recognized，断句发生，可以在这里处理字幕显示逻辑，比如把sourceText和targetText发送到前端显示，然后清空StringBuilder准备下一句的字幕
                            var original = sourceSb.ToString();
                            if (string.IsNullOrEmpty(original))
                                continue;

                            var translatedText = tranlatedSb.ToString();
                            var configTarLang = _translateTarget[sourceLang];
                            // 火山引擎使用AUTO（zhen）时不会将识别的语言返回，所以需要自己检测是中文还是英文，以决定翻译的目标语言
                            var detectTarLang = AUTO.Equals(sourceLang) ? AppConstants.LangCode(LangDetect.DetectZhEn(translatedText)) : configTarLang;
                            var transleteDic = new Dictionary<string, string> { { detectTarLang, translatedText } };

                            Logger.LogDebug("RECOGNIZED in {sourceLang}: Text={original}", sourceLang, original);
                            await BatchTranslateAsync(original, sourceLang, session.TranslatingTime, TimeSpan.FromSeconds(30), speakerId, transleteDic);

                            if (session.RecvAudio.Length > 0)
                            {
                                _ = TextToSpeech(session.RecvAudio.ToArray(), detectTarLang, sourceLang, speakerId);

                                session.RecvAudio.SetLength(0); // 清空缓冲
                                session.RecvAudio.Position = 0;
                            }

                            sourceSb.Clear();
                            tranlatedSb.Clear();
                            session.TranslatingTime = 0;
                        }
                        else
                        {
                            // Regular data/partial (Recognizing)
                            if (resp.Data != null)
                            {
                                await session.RecvAudio.WriteAsync(resp.Data.ToByteArray());
                            }
                            if (!string.IsNullOrEmpty(resp.Text) && new List<EV.Type> { EV.Type.SourceSubtitleResponse, EV.Type.TranslationSubtitleResponse }.Contains(resp.Event))
                            {
                                (resp.Event == EV.Type.SourceSubtitleResponse ? sourceSb : tranlatedSb).Append(resp.Text);

                                session.TranslatingTime = session.TranslatingTime <= 0 ? (ulong)DateTime.Now.Ticks : session.TranslatingTime;
                                var partialText = sourceSb.ToString();
                                var translatedText = tranlatedSb.ToString();

                                var partialLang = AppConstants.LangCode(LangDetect.DetectZhEn(partialText));
                                var translatedLang = AppConstants.LangCode(LangDetect.DetectZhEn(translatedText));
                                var langDic = new Dictionary<string, string> { { sourceLang, partialText } };
                                langDic[partialLang] = partialText;
                                langDic[translatedLang] = translatedText;

                                var captions = BuildTextDictionary(langDic, sourceLang, partialText);

                                Logger.LogDebug("RECOGNIZING in {sourceLang}: Text={Text}", sourceLang, partialText);

                                await Transcript(captions, false, session.TranslatingTime, TimeSpan.FromSeconds(30), sourceLang, partialText, speakerId);
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
                session.SessionEnded.TrySetResult(true);
            }
        }

        private async Task Reconnect(SpeakerSession session, ClientWebSocket? wsClient, string sourceLang, string speakerId)
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
                session.WsClients[sourceLang] = newClient;
                session.SessionIds[sourceLang] = sessionId;

                // Replace handshake tcs before StartSession so SessionStarted can complete the correct task
                var handshakeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                session.HandshakeDones[sourceLang] = handshakeTcs;

                // Start new receive loop and re-send StartSession to re-handshake
                _ = ReceiveMessage(session, sessionId, sourceLang, speakerId);
                await StartSession(session, sessionId, sourceLang).ConfigureAwait(false);
                // Wait handshake
                await handshakeTcs.Task.ConfigureAwait(false);

                if (session.FinishFlag)
                    await FinishSession(session, sourceLang);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Reconnect failed for session {sessionId} sourceLang {sourceLang}", session.SessionIds[sourceLang], sourceLang);
                session.SessionEnded.TrySetResult(true);
            }
        }

        protected override async Task TextToSpeech(string text, string lang, string sourceLang, string speakerId)
        {
            await Task.CompletedTask;
        }

        private void StartKeepAliveLoop(SpeakerSession session)
        {
            if (session.KeepAliveTask != null)
                return;

            session.KeepAliveTask = Task.Run(async () =>
            {
                var silenceChunk = new byte[ChunkSize];

                while (!session.KeepAliveCts.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(SilenceSendInterval, session.KeepAliveCts.Token).ConfigureAwait(false);

                        if (session.SessionEnded.Task.IsCompleted)
                            continue;

                        var lastAudioTicks = Volatile.Read(ref session.LastAudioTicks);
                        if (lastAudioTicks == 0)
                            continue;

                        var idle = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - lastAudioTicks);
                        if (idle < SilenceStartAfterNoAudio)
                            continue;

                        if (!session.WsClients.Values.All(c => c.State == WebSocketState.Open))
                            continue;

                        await SendChunkAsync(session, silenceChunk).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogDebug(ex, "Send silence chunk failed for speaker {speakerId}", session.SpeakerId);
                    }
                }
            });
        }

        private async Task SendChunkAsync(SpeakerSession session, byte[] chunk)
        {
            var tasks = _translateTarget.Keys.Select(l =>
            {
                var chunkReq = new TranslateRequest
                {
                    RequestMeta = new RequestMeta { SessionID = session.SessionIds[l] },
                    Event = EV.Type.TaskRequest,
                    SourceAudio = new Audio { BinaryData = ByteString.CopyFrom(chunk) }
                };

                return session.WsClients[l].SendAsync(new ArraySegment<byte>(chunkReq.ToByteArray()), WebSocketMessageType.Binary, true, new CancellationTokenSource(DefaultTimeout).Token);
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        private static byte[] GetBatchBuffer(SpeakerSession session, byte[]? buffer, int bufferLength)
        {
            // Append incoming 640-byte buffer and send only when we have exactly one full ChunkSize (5 * 640).
            byte[] chunkToSend = null;
            lock (session.SendLock)
            {
                // Copy incoming data (assumed to be 640 bytes) into send buffer
                Array.Copy(buffer, 0, session.SendBuffer, session.SendLen, bufferLength);
                session.SendLen += bufferLength;

                // When we've accumulated exactly ChunkSize, prepare chunk and reset
                if (session.SendLen >= ChunkSize)
                {
                    chunkToSend = new byte[ChunkSize];
                    Array.Copy(session.SendBuffer, 0, chunkToSend, 0, ChunkSize);
                    session.SendLen = 0;
                }
            }
            return chunkToSend;
        }

        public override async Task AddPhrases(IEnumerable<string> phrases)
        {
            _hotWords.AddRange(phrases);
            var tasks = _speakerSessions.Values.SelectMany(s => _translateTarget.Keys.Select(l => UpdateSession(s, l)));
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        public override async Task ShutDownAsync()
        {
            await base.ShutDownAsync().ConfigureAwait(false);

            var sessions = _speakerSessions.Values.ToArray();
            _speakerSessions.Clear();

            var shutdownTasks = sessions.Select(ShutDownSessionAsync);
            await Task.WhenAll(shutdownTasks).ConfigureAwait(false);
            await CacheHelper.DeleteAsync(CacheConstants.CallParticipantsKey(ThreadId, null)).ConfigureAwait(false);
        }

        public override async Task ShutDownSessionAsync(string speakerId)
        {
            if (string.IsNullOrWhiteSpace(speakerId))
                return;

            if (!_speakerSessions.TryRemove(speakerId, out var session))
                return;

            await ShutDownSessionAsync(session).ConfigureAwait(false);
        }

        private async Task ShutDownSessionAsync(SpeakerSession session)
        {
            session.FinishFlag = true;
            session.KeepAliveCts.Cancel();

            var finishTasks = _translateTarget.Keys.Select(lang => FinishSession(session, lang));
            await Task.WhenAll(finishTasks).ConfigureAwait(false);
        }
    }
}
