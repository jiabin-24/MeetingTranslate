using EchoBot.Models;
using EchoBot.WebRTC;
using Microsoft.AspNetCore.Mvc;

namespace EchoBot.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AcsController : ControllerBase
    {
        [HttpPost("callback")]
        public IActionResult Callback()
        {
            // 回调占位：生产中可解析 CloudEvents 并根据状态驱动流程
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

        [HttpGet("ensureGroupCallConnectionAsync")]
        public async Task<bool> EnsureGroupCallConnectionAsync(string threadId, string targetLang)
        {
            var rtcSessionManager = RtcSessionManagerRegistry.TryGet(threadId, targetLang, out var manager) ? manager : null;
            if (rtcSessionManager == null) return false;

            Thread.Sleep(2000);
            await rtcSessionManager.EnsureGroupCallConnectionAsync();
            return true;
        }
    }
}