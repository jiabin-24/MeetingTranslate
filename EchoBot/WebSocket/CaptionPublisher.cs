using EchoBot.Util;
using Microsoft.AspNetCore.SignalR;
using static EchoBot.Models.Caption;

namespace EchoBot.WebSocket
{
    public interface ICaptionPublisher
    {
        Task PublishCaptionAsync(CaptionPayload payload);

        Task PublishAudioAsync(string meetingId, string audioId, byte[] audio, string speakerId, string lang, string contentType, int length, string headerHex);
    }

    public class CaptionPublisher : ICaptionPublisher
    {
        private readonly IHubContext<CaptionSignalRHub>? _hub;
        private readonly CaptionHub? _wsHub;

        public static CaptionPublisher CreateInstance(bool useSignalR)
        {
            if (useSignalR)
            {
                var hub = ServiceLocator.GetRequiredService<IHubContext<CaptionSignalRHub>>();
                return new CaptionPublisher(hub);
            }
            else
            {
                var ws = ServiceLocator.GetRequiredService<CaptionHub>();
                return new CaptionPublisher(ws);
            }
        }

        // SignalR constructor
        private CaptionPublisher(IHubContext<CaptionSignalRHub> hub)
        {
            _hub = hub;
        }

        // WebSocket constructor
        private CaptionPublisher(CaptionHub wsHub)
        {
            _wsHub = wsHub;
        }

        public Task PublishCaptionAsync(CaptionPayload payload)
        {
            if (_hub != null)
            {
                return _hub.Clients.Group(payload.MeetingId).SendCoreAsync("caption", new object?[] { payload }, default);
            }

            if (_wsHub != null)
            {
                return _wsHub.BroadcastAsync(payload);
            }

            return Task.CompletedTask;
        }

        public Task PublishAudioAsync(string meetingId, string audioId, byte[] audio, string speakerId, string lang, string contentType, int length, string headerHex)
        {
            if (_hub != null)
            {
                var meta = new { type = "audio", meetingId, audioId, speakerId, lang, contentType, length, headerHex, isFinal = true };
                return _hub.Clients.Group(meetingId).SendCoreAsync("audio", new object?[] { meta, audio }, default);
            }

            if (_wsHub != null)
            {
                return _wsHub.BroadcastAudioAsync(meetingId, audioId, audio, speakerId, lang, contentType, length, headerHex);
            }

            return Task.CompletedTask;
        }
    }
}
