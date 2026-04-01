namespace EchoBot.Bot
{
    public interface ICallNotificationQueue
    {
        string InstanceId { get; }

        Task EnqueueForInstanceAsync(string instanceId, string payload);
    }
}
