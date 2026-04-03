using EchoBot.Bot;
using EchoBot.Constants;
using EchoBot.Models;
using EchoBot.Util;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Graph.Communications.Client;
using StackExchange.Redis;

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
            var (payload, callId) = await JoinInfo.ReadPayloadAndCallIdAsync(this.Request).ConfigureAwait(false);

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
                        await _callNotificationQueue.EnqueueForInstanceAsync(ownerInstance, new QueuedCallNotification
                        {
                            Payload = payload,
                            Authorization = this.Request.Headers.Authorization.ToString(),
                            ContentType = this.Request.ContentType
                        }).ConfigureAwait(false);

                        _logger.LogInformation("Notification queued to owner instance. CallId={CallId}, Current={CurrentInstance}, Owner={OwnerInstance}", callId, _instanceId, ownerInstance);
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
    }
}