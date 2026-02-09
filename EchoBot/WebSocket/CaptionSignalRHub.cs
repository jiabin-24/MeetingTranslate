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
            if (!Context.Items.ContainsKey("UserId")) throw new HubException("Unauthorized");

            // If this connection previously subscribed to a language group for a (possibly) different meeting,
            // remove it before adding the new language group so the connection does not receive audio for the old language.
            try
            {
                if (Context.Items.ContainsKey("TargetLang") || Context.Items.ContainsKey("MeetingId"))
                {
                    var prevTarget = Context.Items.ContainsKey("TargetLang") ? Context.Items["TargetLang"] as string : null;
                    var prevMeeting = Context.Items.ContainsKey("MeetingId") ? Context.Items["MeetingId"] as string : null;

                    if (!string.IsNullOrEmpty(prevTarget))
                    {
                        // Remove previous language group if it's different from the new one or meeting changed
                        if (!string.Equals(prevTarget, targetLang, StringComparison.Ordinal) || !string.Equals(prevMeeting, meetingId, StringComparison.Ordinal))
                        {
                            try { await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"{prevMeeting}:{prevTarget}"); } catch { /* ignore */ }
                        }
                    }
                }
            }
            catch
            {
                // ignore failures to remove old groups
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, meetingId);

            if (!string.IsNullOrEmpty(targetLang))
            {
                // Also add connection to a language-specific audio group: "{meetingId}:{targetLang}"
                await Groups.AddToGroupAsync(Context.ConnectionId, $"{meetingId}:{targetLang}");
            }

            Context.Items["MeetingId"] = meetingId;
            Context.Items["TargetLang"] = targetLang;

            await Clients.Caller.SendAsync("subscribed");
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            await base.OnDisconnectedAsync(exception);
        }
    }
}
