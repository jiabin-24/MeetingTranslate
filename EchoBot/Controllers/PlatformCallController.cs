using EchoBot.Bot;
using EchoBot.Constants;
using EchoBot.Util;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Graph.Communications.Client;
using StackExchange.Redis;
using System.Text;
using System.Text.Json;

namespace EchoBot.Controllers
{
    /// <summary>
    /// Entry point for handling call-related web hook requests from Skype Platform.
    /// </summary>
    [ApiController]
    [Route(HttpRouteConstants.CallSignalingRoutePrefix)]
    public class PlatformCallController(ILogger<PlatformCallController> logger,
        IBotService botService,
        IConnectionMultiplexer mux,
        ICallNotificationQueue callNotificationQueue) : ControllerBase
    {
        private static readonly TimeSpan NotificationOwnerTtl = TimeSpan.FromMinutes(30);
        private readonly ILogger<PlatformCallController> _logger = logger;
        private readonly IBotService _botService = botService;
        private readonly IConnectionMultiplexer _mux = mux;
        private readonly ICallNotificationQueue _callNotificationQueue = callNotificationQueue;
        private readonly string _instanceId = callNotificationQueue.InstanceId;

        /// <summary>
        /// Handle a callback for an incoming call.
        /// </summary>
        /// <returns>The <see cref="HttpResponseMessage" />.</returns>
        [HttpPost]
        [Route(HttpRouteConstants.OnIncomingRequestRoute)]
        public async Task<HttpResponseMessage> OnIncomingRequestAsync()
        {
            return await ProcessNotificationWithOwnerRoutingAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Handle a callback for an existing call
        /// </summary>
        /// <returns>The <see cref="HttpResponseMessage" />.</returns>
        [HttpPost]
        [Route(HttpRouteConstants.OnNotificationRequestRoute)]
        public async Task<HttpResponseMessage> OnNotificationRequestAsync()
        {
            return await ProcessNotificationWithOwnerRoutingAsync().ConfigureAwait(false);
        }

        private async Task<HttpResponseMessage> ProcessNotificationWithOwnerRoutingAsync()
        {
            var (payload, callId) = await ReadPayloadAndExtractCallIdAsync().ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(callId))
            {
                var ownerKey = CacheConstants.CallNotificationOwnerKey(callId);
                var db = _mux.GetDatabase();

                var ownerSet = await db.StringSetAsync(ownerKey, _instanceId, NotificationOwnerTtl, when: When.NotExists).ConfigureAwait(false);
                if (!ownerSet)
                {
                    var owner = await db.StringGetAsync(ownerKey).ConfigureAwait(false);
                    if (owner.HasValue && !owner.ToString().Equals(_instanceId, StringComparison.Ordinal))
                    {
                        var ownerInstance = owner.ToString();
                        _logger.LogInformation("Notification queued to owner instance. CallId={CallId}, Current={CurrentInstance}, Owner={OwnerInstance}", callId, _instanceId, ownerInstance);
                        await _callNotificationQueue.EnqueueForInstanceAsync(ownerInstance, payload).ConfigureAwait(false);
                        return new HttpResponseMessage(System.Net.HttpStatusCode.Accepted);
                    }
                }

                _ = db.KeyExpireAsync(ownerKey, NotificationOwnerTtl);
            }

            if (this.Request.Body.CanSeek)
            {
                this.Request.Body.Position = 0;
            }

            var httpRequestMessage = HttpHelpers.ToHttpRequestMessage(this.Request);
            return await _botService.Client.ProcessNotificationAsync(httpRequestMessage).ConfigureAwait(false);
        }

        private async Task<(string Payload, string? CallId)> ReadPayloadAndExtractCallIdAsync()
        {
            this.Request.EnableBuffering();

            string payload;
            using (var reader = new StreamReader(this.Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
            {
                payload = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            if (this.Request.Body.CanSeek)
            {
                this.Request.Body.Position = 0;
            }

            return (payload, TryExtractCallId(payload));
        }

        private static string? TryExtractCallId(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(payload);
                return FindCallId(doc.RootElement);
            }
            catch
            {
                return null;
            }
        }

        private static string? FindCallId(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        if (property.Value.ValueKind == JsonValueKind.String)
                        {
                            var callId = ExtractCallIdFromText(property.Value.GetString());
                            if (!string.IsNullOrWhiteSpace(callId))
                            {
                                return callId;
                            }
                        }

                        var nestedCallId = FindCallId(property.Value);
                        if (!string.IsNullOrWhiteSpace(nestedCallId))
                        {
                            return nestedCallId;
                        }
                    }
                    break;

                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        var callId = FindCallId(item);
                        if (!string.IsNullOrWhiteSpace(callId))
                        {
                            return callId;
                        }
                    }
                    break;
            }

            return null;
        }

        private static string? ExtractCallIdFromText(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            const string marker = "/communications/calls/";
            var start = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                return null;
            }

            start += marker.Length;
            var end = value.IndexOf('/', start);
            if (end < 0)
            {
                end = value.IndexOf('?', start);
            }
            if (end < 0)
            {
                end = value.Length;
            }

            if (end <= start)
            {
                return null;
            }

            return value[start..end];
        }
    }
}