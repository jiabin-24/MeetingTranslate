namespace EchoBot.Models
{
    public class QueuedCallNotification
    {
        public string Payload { get; set; } = string.Empty;

        public string? Authorization { get; set; }

        public string? ContentType { get; set; }
    }
}
