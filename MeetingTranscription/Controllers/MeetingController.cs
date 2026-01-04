using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using StackExchange.Redis;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static EchoBot.Models.Caption;

namespace MeetingTranscription.Controllers
{
    [Route("api/{controller}")]
    public class MeetingController : Controller
    {
        private readonly IConnectionMultiplexer _mux;

        public MeetingController(IConnectionMultiplexer mux)
        {
            _mux = mux;
        }

        [Route("getMeetingCaptions")]
        public async Task<List<CaptionPayload>> GetMeetingCaptions([FromQuery] string threadId)
        {
            var captions = (await _mux.GetDatabase().ListRangeAsync($"list:{threadId}"))
                .Select(v => JsonConvert.DeserializeObject<CaptionPayload>((string)v)).ToList();
            return captions;
        }
    }
}
