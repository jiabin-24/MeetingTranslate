using EchoBot.Bot;
using EchoBot.Constants;
using EchoBot.Models;
using Microsoft.AspNetCore.Mvc;
using StackExchange.Redis;

namespace EchoBot.Controllers
{
    /// <summary>
    /// Entry point for handling call-related web hook requests from Skype Platform.
    /// </summary>
    [ApiController]
    [Route(HttpRouteConstants.CallSignalingRoutePrefix)]
    public class PlatformCallController(ILogger<PlatformCallController> logger,
        IConnectionMultiplexer mux,
        ICallNotificationQueue callNotificationQueue) : ControllerBase
    {
        private readonly ILogger<PlatformCallController> _logger = logger;
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
            if (string.IsNullOrWhiteSpace(callId))
            {
                _logger.LogWarning("Notification skipped because callId is missing.");
                return new HttpResponseMessage(System.Net.HttpStatusCode.Accepted);
            }

            var ownerKey = CacheConstants.CallNotificationOwnerKey(callId);
            var db = _mux.GetDatabase();
            var owner = await db.StringGetAsync(ownerKey).ConfigureAwait(false);

            if (!owner.HasValue || string.IsNullOrWhiteSpace(owner.ToString()))
            {
                _logger.LogWarning("Notification skipped because owner not found in redis. CallId={CallId}, Current={CurrentInstance}", callId, _instanceId);
                return new HttpResponseMessage(System.Net.HttpStatusCode.Accepted);
            }

            var targetInstance = owner.ToString();

            await _callNotificationQueue.EnqueueForInstanceAsync(targetInstance, new QueuedCallNotification
            {
                Payload = payload,
                Authorization = this.Request.Headers.Authorization.ToString(),
                ContentType = this.Request.ContentType
            }).ConfigureAwait(false);

            _logger.LogInformation("Notification queued. CallId={CallId}, Current={CurrentInstance}, Target={TargetInstance}", callId, _instanceId, targetInstance);
            return new HttpResponseMessage(System.Net.HttpStatusCode.Accepted);
        }
    }
}