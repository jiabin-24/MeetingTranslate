using EchoBot.Authentication;
using Microsoft.AspNetCore.SignalR;

namespace EchoBot.WebSocket
{
    public class CaptionSignalRHub : Hub
    {
        public async Task Auth(string token)
        {
            if (JwtAuth.ValidateToken(token, out var uid))
            {
                Context.Items["UserId"] = uid;
                await Clients.Caller.SendAsync("auth_ok");
            }
            else
            {
                throw new HubException("Unauthorized");
            }
        }

        public async Task Subscribe(string meetingId, string targetLang)
        {
            if (!Context.Items.ContainsKey("UserId"))
                throw new HubException("Unauthorized");

            await Groups.AddToGroupAsync(Context.ConnectionId, meetingId);
            Context.Items["MeetingId"] = meetingId;
            Context.Items["TargetLang"] = targetLang;

            await Clients.Caller.SendAsync("subscribed");
        }

        public override Task OnDisconnectedAsync(Exception? exception)
        {
            // SignalR automatically removes connections from groups on disconnect.
            return base.OnDisconnectedAsync(exception);
        }
    }
}
