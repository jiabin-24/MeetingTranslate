using Microsoft.AspNetCore.Mvc;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using System.Threading.Tasks;

namespace MeetingTranscription.Controllers
{
    // BotController handles incoming HTTP POST requests and delegates them to the CloudAdapter.
    // It acts as an interface between incoming requests and the bot's processing logic for SingleTenant setup.
    [Route("api/messages")]
    [ApiController]
    public class BotController(CloudAdapter adapter, IBot bot) : ControllerBase
    {
        private readonly CloudAdapter adapter = adapter;  // Updated to CloudAdapter for SingleTenant authentication
        private readonly IBot _bot = bot;

        [HttpPost]
        [HttpGet]
        public async Task PostAsync()
        {
            // Delegate the processing of the HTTP POST to the adapter.
            // The adapter will invoke the bot.
            await adapter.ProcessAsync(Request, Response, _bot);
        }
    }
}
