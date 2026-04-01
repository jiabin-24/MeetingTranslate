using EchoBot.Constants;
using Microsoft.Graph.Communications.Client;
using StackExchange.Redis;
using System.Text;

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

        public async Task EnqueueForInstanceAsync(string instanceId, string payload)
        {
            var db = _mux.GetDatabase();
            await db.ListRightPushAsync(CacheConstants.CallNotificationQueueKey(instanceId), payload).ConfigureAwait(false);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var db = _mux.GetDatabase();
            var queueKey = CacheConstants.CallNotificationQueueKey(InstanceId);

            while (!stoppingToken.IsCancellationRequested)
            {
                var payload = await db.ListLeftPopAsync(queueKey).ConfigureAwait(false);
                if (!payload.HasValue)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, "https://localhost/api/calling/notification")
                    {
                        Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json")
                    };

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
