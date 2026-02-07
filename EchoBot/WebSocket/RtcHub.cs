using EchoBot.WebRTC;
using Microsoft.AspNetCore.SignalR;

namespace EchoBot.WebSocket
{
    public class RtcHub : Hub
    {
        private readonly RtcSessionManager _sessions;

        public RtcHub(RtcSessionManager sessions) => _sessions = sessions;

        public Task<string> Offer(string sdpOffer) => _sessions.CreateOrUpdateAsync(Context.ConnectionId, sdpOffer);

        public Task Ice(string candidate) => _sessions.AddIceCandidateAsync(Context.ConnectionId, candidate);

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            await _sessions.CloseAsync(Context.ConnectionId);
            await base.OnDisconnectedAsync(exception);
        }
    }
}
