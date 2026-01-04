using EchoBot.WebSocket;
using static EchoBot.Models.Caption;

public class CaptionPublisher
{
    private readonly CaptionHub _hub;
    public CaptionPublisher(CaptionHub hub) => _hub = hub;

    public Task PublishCaptionAsync(
        string meetingId,
        string text,
        string lang,
        string targetLang,
        bool isFinal,
        long? startMs = null,
        long? endMs = null,
        string? speaker = null)
    {
        var payload = new CaptionPayload(
            Type: "caption",
            MeetingId: meetingId,
            Speaker: speaker,
            Lang: lang,
            TargetLang: targetLang,
            Text: text,
            IsFinal: isFinal,
            StartMs: startMs,
            EndMs: endMs
        );

        return _hub.BroadcastAsync(payload);
    }

    public Task PublishCaptionAsync(CaptionPayload payload)
    {
        return _hub.BroadcastAsync(payload);
    }
}
