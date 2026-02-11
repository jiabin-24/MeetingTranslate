using EchoBot.Util;
using EchoBot.WebRTC;
using Microsoft.AspNetCore.Mvc;

namespace EchoBot.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AcsController : ControllerBase
    {
        private readonly RtcSessionManager _rtcSessionManager;

        private ILogger _logger;

        private readonly IConfiguration _config;

        private readonly CacheHelper _cache;

        public AcsController(RtcSessionManager rtcSessionManager, IConfiguration config, ILogger<AcsController> logger,
            CacheHelper cache)
        {
            _rtcSessionManager = rtcSessionManager;
            _config = config;
            _logger = logger;
            _cache = cache;
        }

        [HttpPost("callback")]
        public IActionResult Callback()
        {
            // 回调占位：生产中可解析 CloudEvents 并根据状态驱动流程
            return Ok();
        }

        [HttpPost("addParticipant")]
        public async Task<IActionResult> AddParticipant(string groupId)
        {
            var roomParticipant = await _rtcSessionManager.AddParticipant(groupId);

            return Ok(roomParticipant);
        }
    }
}