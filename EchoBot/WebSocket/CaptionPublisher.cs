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

    public Task PublishAudioAsync(string meetingId, string audioId, byte[] audio, string contentType, int length, string headerHex)
    {
        return _hub.BroadcastAudioAsync(meetingId, audioId, audio, contentType, length, headerHex);
    }
}
