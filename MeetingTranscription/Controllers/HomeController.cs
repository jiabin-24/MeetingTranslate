using MeetingTranscription.Helpers;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace MeetingTranscription.Controllers
{
    public class HomeController(ConcurrentDictionary<string, string> transcriptsDictionary)
        : Controller
    {
        /// <summary>
        /// Stores the Azure configuration values.
        /// </summary>
        private readonly GraphHelper _graphHelper = new();

        /// <summary>
        /// Returns view to be displayed in Task Module.
        /// </summary>
        /// <param name="meetingId">Id of the meeting.</param>
        /// <returns></returns>
        [HttpGet("Home/Index/{meetingId?}")]
        public async Task<IActionResult> Index([FromRoute] string meetingId)
        {
            ViewBag.Transcripts = "Transcript not found.";

            if (!string.IsNullOrEmpty(meetingId))
            {
                var isFound = transcriptsDictionary.TryGetValue(meetingId, out string transcripts);
                if (isFound)
                {
                    ViewBag.Transcripts = $"Format: {transcripts}";
                }
                else
                {
                    var result = await _graphHelper.GetMeetingTranscriptionsAsync(meetingId);
                    if (!string.IsNullOrEmpty(meetingId))
                    {
                        transcriptsDictionary.AddOrUpdate(meetingId, result, (key, newValue) => result);
                        ViewBag.Transcripts = $"Format: {result}";
                    }
                }
            }
            return View();
        }
    }
}