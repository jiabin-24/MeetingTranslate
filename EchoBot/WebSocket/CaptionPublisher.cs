using EchoBot.Util;
using Microsoft.AspNetCore.SignalR;
using static EchoBot.Models.Caption;

namespace EchoBot.WebSocket
{
    public interface ICaptionPublisher
    {
        Task PublishCaptionAsync(CaptionPayload payload);
    }

    public class CaptionPublisher : ICaptionPublisher
    {
        private readonly IHubContext<CaptionSignalRHub>? _sigHub;

        public static CaptionPublisher CreateInstance()
        {
            var sigHub = ServiceLocator.GetRequiredService<IHubContext<CaptionSignalRHub>>();
            return new CaptionPublisher(sigHub);
        }

        // SignalR constructor
        private CaptionPublisher(IHubContext<CaptionSignalRHub> hub)
        {
            _sigHub = hub;
        }

        public async Task PublishCaptionAsync(CaptionPayload payload)
        {
            await _sigHub.Clients.Group(payload.MeetingId).SendCoreAsync("caption", [payload], default);
        }
    }
}
