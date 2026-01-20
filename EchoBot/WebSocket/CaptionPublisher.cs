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
        private readonly IHubContext<CaptionSignalRHub>? _sigHub;
        private readonly CaptionHub? _wsHub;

        public static CaptionPublisher CreateInstance(bool useSignalR)
        {
            if (useSignalR)
            {
                var sigHub = ServiceLocator.GetRequiredService<IHubContext<CaptionSignalRHub>>();
                return new CaptionPublisher(sigHub);
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
            _sigHub = hub;
        }

        // WebSocket constructor
        private CaptionPublisher(CaptionHub wsHub)
        {
            _wsHub = wsHub;
        }

        public Task PublishCaptionAsync(CaptionPayload payload)
        {
            if (_sigHub != null)
            {
                return _sigHub.Clients.Group(payload.MeetingId).SendCoreAsync("caption", new object?[] { payload }, default);
            }

            if (_wsHub != null)
            {
                return _wsHub.BroadcastAsync(payload);
            }

            return Task.CompletedTask;
        }

        public Task PublishAudioAsync(string meetingId, string audioId, byte[] audio, string speakerId, string lang, string contentType, int length, string headerHex)
        {
            if (_sigHub != null)
            {
                var meta = new { type = "audio", meetingId, audioId, speakerId, lang, contentType, length, headerHex, isFinal = true };
                return _sigHub.Clients.Group(meetingId).SendCoreAsync("audio", new object?[] { meta, audio }, default);
            }

            if (_wsHub != null)
            {
                return _wsHub.BroadcastAudioAsync(meetingId, audioId, audio, speakerId, lang, contentType, length, headerHex);
            }

            return Task.CompletedTask;
        }
    }
}
