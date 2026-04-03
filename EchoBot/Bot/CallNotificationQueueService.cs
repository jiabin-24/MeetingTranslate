using Azure;
using Azure.Identity;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using EchoBot.Models;
using Microsoft.Graph.Communications.Client;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EchoBot.Bot
{
    public class CallNotificationQueueService(ILogger<CallNotificationQueueService> logger,
        IConfiguration configuration,
        IBotService botService) : BackgroundService, ICallNotificationQueue
    {
        private readonly ILogger<CallNotificationQueueService> _logger = logger;
        private readonly IBotService _botService = botService;

        private readonly string _queuePrefix = configuration["AzureStorage:QueuePrefix"] ?? "call-notification";
        private readonly ConcurrentDictionary<string, QueueClient> _queueClients = new();

        private readonly QueueServiceClient _queueServiceClient = CreateQueueServiceClient(configuration);

        public string InstanceId { get; } = Environment.MachineName;

        private static QueueServiceClient CreateQueueServiceClient(IConfiguration configuration)
        {
            var connectionString = configuration["AzureStorage:ConnectionString"];
            if (!string.IsNullOrEmpty(connectionString))
                return new QueueServiceClient(connectionString);

            return new QueueServiceClient(QueueServiceUri(configuration), new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ManagedIdentityClientId = configuration["VmUserAssignedIdentity:Storage"]
            }));
        }

        private static Uri QueueServiceUri(IConfiguration configuration) => new($"https://{configuration["AzureStorage:Account"] ?? throw new InvalidOperationException("Missing storage queue service uri. Configure 'AzureStorage:Account'")}.queue.core.windows.net");

        public async Task EnqueueForInstanceAsync(string instanceId, QueuedCallNotification notification)
        {
            try
            {
                var serialized = JsonSerializer.Serialize(notification);
                var queue = GetQueueClient(instanceId);
                await queue.CreateIfNotExistsAsync().ConfigureAwait(false);
                await queue.SendMessageAsync(serialized).ConfigureAwait(false);
            }
            catch (RequestFailedException ex)
            {
                LogQueueRequestFailed("enqueue", ex, instanceId);
                throw;
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var queue = GetQueueClient(InstanceId);
            try
            {
                await queue.CreateIfNotExistsAsync(cancellationToken: stoppingToken).ConfigureAwait(false);
                _logger.LogInformation("Call notification queue worker started. InstanceId={InstanceId}, QueueName={QueueName}", InstanceId, queue.Name);
            }
            catch (RequestFailedException ex)
            {
                LogQueueRequestFailed("initialize worker queue", ex, InstanceId);
                throw;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                QueueMessage[] messages;
                try
                {
                    messages = await queue.ReceiveMessagesAsync(
                        maxMessages: 1,
                        visibilityTimeout: TimeSpan.FromMinutes(1),
                        cancellationToken: stoppingToken).ConfigureAwait(false);
                }
                catch (RequestFailedException ex)
                {
                    LogQueueRequestFailed("receive message", ex, InstanceId);
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (messages.Length == 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var message = messages[0];

                try
                {
                    var notification = JsonSerializer.Deserialize<QueuedCallNotification>(message.MessageText);
                    if (notification == null || string.IsNullOrWhiteSpace(notification.Payload))
                    {
                        _logger.LogWarning("Skipped invalid queued call notification for instance {InstanceId}", InstanceId);
                        await queue.DeleteMessageAsync(message.MessageId, message.PopReceipt, stoppingToken).ConfigureAwait(false);
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

                    await queue.DeleteMessageAsync(message.MessageId, message.PopReceipt, stoppingToken).ConfigureAwait(false);
                }
                catch (RequestFailedException ex)
                {
                    LogQueueRequestFailed("process queued notification", ex, InstanceId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to process queued call notification for instance {InstanceId}", InstanceId);
                }
            }
        }

        private QueueClient GetQueueClient(string instanceId)
        {
            return _queueClients.GetOrAdd(instanceId, id =>
            {
                var queueName = BuildQueueName(id);
                return _queueServiceClient.GetQueueClient(queueName);
            });
        }

        private string BuildQueueName(string instanceId)
        {
            // Queue names must be 3-63 characters, lowercase, numbers, and hyphens only, start and end with a letter or number, and not contain consecutive hyphens.
            // See https://learn.microsoft.com/en-us/rest/api/storageservices/naming-queues-and-metadata#queue-names for details.
            var source = $"{_queuePrefix}-{instanceId}".ToLowerInvariant();
            source = Regex.Replace(source, "[^a-z0-9-]", "-");
            source = Regex.Replace(source, "-+", "-").Trim('-');

            if (source.Length < 3)
            {
                source = $"{_queuePrefix}-node";
            }

            if (source.Length > 63)
            {
                source = source[..63].Trim('-');
            }

            return source;
        }

        private void LogQueueRequestFailed(string operation, RequestFailedException ex, string instanceId)
        {
            if (ex.Status == 401 || ex.Status == 403)
            {
                _logger.LogError(ex, "Storage queue access denied. Op={Operation}, InstanceId={InstanceId}, Status={Status}, ErrorCode={ErrorCode}", operation, instanceId, ex.Status, ex.ErrorCode);
                return;
            }

            _logger.LogError(ex, "Storage queue request failed. Op={Operation}, InstanceId={InstanceId}, Status={Status}, ErrorCode={ErrorCode}", operation, instanceId, ex.Status, ex.ErrorCode);
        }

    }
}
