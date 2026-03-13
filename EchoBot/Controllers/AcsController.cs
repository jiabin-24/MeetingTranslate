using EchoBot.Models;
using EchoBot.WebRTC;
using Microsoft.AspNetCore.Mvc;

namespace EchoBot.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AcsController : ControllerBase
    {
        private readonly ILogger<AcsController> _logger;

        public AcsController(ILogger<AcsController> logger)
        {
            _logger = logger;
        }

        [HttpPost("callback")]
        public async Task<IActionResult> Callback()
        {
            using var sr = new StreamReader(Request.Body);
            var body = await sr.ReadToEndAsync();
            _logger.LogInformation("ACS callback received. Headers: {headers}; Body: {body}", string.Join(";", Request.Headers.Select(h => $"{h.Key}={h.Value}")), body);

            return Ok();
        }

        [HttpPost("addParticipant")]
        public async Task<Room> AddParticipant([FromBody] AddRoomParticipant addRoomPart)
        {
            var rtcSessionManager = RtcSessionManagerRegistry.TryGet(addRoomPart.GroupId, addRoomPart.Lang, out var manager) ? manager : null;
            if (rtcSessionManager == null)
            {
                rtcSessionManager = new RtcSessionManager(addRoomPart.GroupId, addRoomPart.Lang);
                RtcSessionManagerRegistry.Register(addRoomPart.GroupId, addRoomPart.Lang, rtcSessionManager);
            }

            var roomParticipant = await rtcSessionManager.AddRoomParticipant(addRoomPart);
            return roomParticipant;
        }
    }
}