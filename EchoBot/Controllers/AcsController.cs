using EchoBot.Models;
using EchoBot.WebRTC;
using Microsoft.AspNetCore.Mvc;

namespace EchoBot.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AcsController(RtcSessionManager rtcSessionManager) : ControllerBase
    {
        private readonly RtcSessionManager _rtcSessionManager = rtcSessionManager;

        [HttpPost("callback")]
        public IActionResult Callback()
        {
            // 回调占位：生产中可解析 CloudEvents 并根据状态驱动流程
            return Ok();
        }

        [HttpPost("addParticipant")]
        public async Task<IActionResult> AddParticipant([FromBody] AddRoomParticipant addRoomParticipant)
        {
            var roomParticipant = await _rtcSessionManager.AddRoomParticipant(addRoomParticipant.GroupId, addRoomParticipant.Lang, addRoomParticipant.ParticipantId);

            return Ok(roomParticipant);
        }
    }
}