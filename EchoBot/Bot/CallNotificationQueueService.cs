using EchoBot.Constants;
using EchoBot.Models;
using Microsoft.Graph.Communications.Client;
using StackExchange.Redis;
using System.Text;
using System.Text.Json;

namespace EchoBot.Bot
{
    public class CallNotificationQueueService(
        ILogger<CallNotificationQueueService> logger,
        IConnectionMultiplexer mux,
        IBotService botService) : BackgroundService, ICallNotificationQueue
    {
        private readonly ILogger<CallNotificationQueueService> _logger = logger;
        private readonly IConnectionMultiplexer _mux = mux;
        private readonly IBotService _botService = botService;

        public string InstanceId { get; } = $"{Environment.MachineName}:{Environment.ProcessId}";

        public async Task EnqueueForInstanceAsync(string instanceId, QueuedCallNotification notification)
        {
            var db = _mux.GetDatabase();
            var serialized = JsonSerializer.Serialize(notification);
            await db.ListRightPushAsync(CacheConstants.CallNotificationQueueKey(instanceId), serialized).ConfigureAwait(false);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var db = _mux.GetDatabase();
            var queueKey = CacheConstants.CallNotificationQueueKey(InstanceId);

            while (!stoppingToken.IsCancellationRequested)
            {
                var queuedValue = await db.ListLeftPopAsync(queueKey).ConfigureAwait(false);
                if (!queuedValue.HasValue)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    var notification = JsonSerializer.Deserialize<QueuedCallNotification>(queuedValue.ToString());
                    if (notification == null || string.IsNullOrWhiteSpace(notification.Payload))
                    {
                        _logger.LogWarning("Skipped invalid queued call notification for instance {InstanceId}", InstanceId);
                        continue;
                    }

                    using var request = new HttpRequestMessage(HttpMethod.Post, "https://localhost/api/calling/notification")
                    {
                        Content = new StringContent(notification.Payload, Encoding.UTF8, string.IsNullOrWhiteSpace(notification.ContentType) ? "application/json" : notification.ContentType)
                    };

                    if (!string.IsNullOrWhiteSpace(notification.Authorization))
                    {
                        request.Headers.TryAddWithoutValidation("Authorization", notification.Authorization);
                    }

                    using var response = await _botService.Client.ProcessNotificationAsync(request).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("Queued notification processing returned status {StatusCode}", response.StatusCode);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to process queued call notification for instance {InstanceId}", InstanceId);
                }
            }
        }
    }
}
