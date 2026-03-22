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
            var rtcSessionManager = RtcSessionManagerRegistry.TryRegister(addRoomPart.GroupId, addRoomPart.Lang, () => new RtcSessionManager(addRoomPart.GroupId, addRoomPart.Lang));
            var roomParticipant = await rtcSessionManager.AddRoomParticipant(addRoomPart);
            return roomParticipant;
        }
    }
}