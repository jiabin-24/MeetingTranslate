using EchoBot.Bot;
using EchoBot.Constants;
using EchoBot.Util;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Graph.Communications.Client;
using Newtonsoft.Json;

namespace EchoBot.Controllers
{
    /// <summary>
    /// Entry point for handling call-related web hook requests from Skype Platform.
    /// </summary>
    [ApiController]
    [Route(HttpRouteConstants.CallSignalingRoutePrefix)]
    public class PlatformCallController : ControllerBase
    {
        private readonly ILogger<PlatformCallController> _logger;
        private readonly AppSettings _settings;
        private readonly IBotService _botService;

        public PlatformCallController(ILogger<PlatformCallController> logger,
            IOptions<AppSettings> settings,
            IBotService botService)
        {
            _logger = logger;
            _settings = settings.Value;
            _botService = botService;
        }

        /// <summary>
        /// Handle a callback for an incoming call.
        /// </summary>
        /// <returns>The <see cref="HttpResponseMessage" />.</returns>
        [HttpPost]
        [Route(HttpRouteConstants.OnIncomingRequestRoute)]
        public async Task<HttpResponseMessage> OnIncomingRequestAsync()
        {
            _logger.LogInformation(JsonConvert.SerializeObject(this.Request), new JsonSerializerSettings
            {
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore,   // 忽略循环
                NullValueHandling = NullValueHandling.Ignore,           // 可选：忽略 null
                Formatting = Formatting.None,                             // 或 Indented 美化
                MaxDepth = 5
            });
            var httpRequestMessage = HttpHelpers.ToHttpRequestMessage(this.Request);
            return await _botService.Client.ProcessNotificationAsync(httpRequestMessage).ConfigureAwait(false);
        }

        /// <summary>
        /// Handle a callback for an existing call
        /// </summary>
        /// <returns>The <see cref="HttpResponseMessage" />.</returns>
        [HttpPost]
        [Route(HttpRouteConstants.OnNotificationRequestRoute)]
        public async Task<HttpResponseMessage> OnNotificationRequestAsync()
        {
            _logger.LogInformation(JsonConvert.SerializeObject(this.Request), new JsonSerializerSettings
            {
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore,   // 忽略循环
                NullValueHandling = NullValueHandling.Ignore,           // 可选：忽略 null
                Formatting = Formatting.None,                             // 或 Indented 美化
                MaxDepth = 5
            });
            var httpRequestMessage = HttpHelpers.ToHttpRequestMessage(this.Request);

            // Pass the incoming notification to the sdk. The sdk takes care of what to do with it.
            return await _botService.Client.ProcessNotificationAsync(httpRequestMessage).ConfigureAwait(false);
        }
    }
}