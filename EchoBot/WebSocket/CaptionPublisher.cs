using EchoBot.WebSocket;
using static EchoBot.Models.Caption;

public class CaptionPublisher
{
    private readonly CaptionHub _hub;
    public CaptionPublisher(CaptionHub hub) => _hub = hub;

    public Task PublishCaptionAsync(CaptionPayload payload)
    {
        return _hub.BroadcastAsync(payload);
    }
}
