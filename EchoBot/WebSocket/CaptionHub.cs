using EchoBot.Authentication;
using Microsoft.AspNetCore.Http;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using static EchoBot.Models.Caption;

namespace EchoBot.WebSocket
{
    public class CaptionHub
    {
        private class ClientMeta
        {
            public string? UserId { get; set; }
            public string? MeetingId { get; set; }
            public string? TargetLang { get; set; }
            public bool Authed { get; set; }
        }

        private readonly ConcurrentDictionary<System.Net.WebSockets.WebSocket, ClientMeta> _clients = new();

        public async Task HandleAsync(HttpContext ctx)
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var meta = new ClientMeta();
            _clients.TryAdd(ws, meta);

            var buffer = new byte[8192];

            // 心跳：防止代理/NAT断开
            var heartbeat = Task.Run(async () =>
            {
                while (ws.State == WebSocketState.Open)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30));
                    try
                    {
                        var ping = Encoding.UTF8.GetBytes("ping");
                        await ws.SendAsync(ping, WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                    catch { break; }
                }
            });

            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close) break;

                    var text = Encoding.UTF8.GetString(buffer.AsSpan(0, result.Count));
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    // 解析首条 "auth" 消息
                    if (!meta.Authed)
                    {
                        try
                        {
                            var auth = JsonSerializer.Deserialize<AuthMessage>(text);
                            if (auth?.Type?.Equals("auth", StringComparison.OrdinalIgnoreCase) == true &&
                                JwtAuth.ValidateToken(auth.Token, out var uid))
                            {
                                meta.UserId = uid;
                                meta.Authed = true;
                                // 可选：返回鉴权成功
                                var ok = Encoding.UTF8.GetBytes("{\"type\":\"auth_ok\"}");
                                await ws.SendAsync(ok, WebSocketMessageType.Text, true, CancellationToken.None);
                                continue;
                            }
                            else
                            {
                                await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Unauthorized", CancellationToken.None);
                                break;
                            }
                        }
                        catch
                        {
                            await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Bad auth message", CancellationToken.None);
                            break;
                        }
                    }

                    // 解析订阅消息：{ "type":"subscribe", "meetingId":"...", "targetLang":"zh" }
                    try
                    {
                        var sub = JsonSerializer.Deserialize<SubscribeMessage>(text);
                        if (sub?.Type?.Equals("subscribe", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            meta.MeetingId = sub.MeetingId;
                            meta.TargetLang = sub.TargetLang;
                            var ack = Encoding.UTF8.GetBytes("{\"type\":\"subscribed\"}");
                            await ws.SendAsync(ack, WebSocketMessageType.Text, true, CancellationToken.None);
                        }
                    }
                    catch { /* 忽略格式错误 */ }
                }
            }
            finally
            {
                _clients.TryRemove(ws, out _);
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
            }
        }

        public async Task BroadcastAsync(CaptionPayload payload)
        {
            var json = JsonSerializer.Serialize(payload);
            var data = Encoding.UTF8.GetBytes(json);

            foreach (var kv in _clients)
            {
                var ws = kv.Key;
                var meta = kv.Value;

                if (ws.State != WebSocketState.Open) continue;
                if (!meta.Authed) continue;
                if (!string.Equals(meta.MeetingId, payload.MeetingId, StringComparison.Ordinal)) continue;

                // 语言过滤（若服务端未预过滤）
                if (meta.TargetLang is { } tl && payload.TargetLang is { } pl &&
                    !string.Equals(tl, pl, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    await ws.SendAsync(data, WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch
                {
                    // 发送失败的连接留待下一轮清理
                }
            }
        }
    }
}
