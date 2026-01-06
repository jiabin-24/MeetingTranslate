using MeetingTranscription.Helpers;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace MeetingTranscription.Controllers
{
    public class HomeController : Controller
    {
        private readonly ConcurrentDictionary<string, string> _transcriptsDictionary;

        /// <summary>
        /// Stores the Azure configuration values.
        /// </summary>
        private readonly GraphHelper _graphHelper;

        public HomeController(ConcurrentDictionary<string, string> transcriptsDictionary)
        {
            _graphHelper = new();
            _transcriptsDictionary = transcriptsDictionary;
        }

        /// <summary>
        /// Returns view to be displayed in Task Module.
        /// </summary>
        /// <param name="meetingId">Id of the meeting.</param>
        /// <returns></returns>
        public async Task<IActionResult> Index([FromQuery] string meetingId)
        {
            ViewBag.Transcripts = "Transcript not found.";

            if (!string.IsNullOrEmpty(meetingId))
            {
                var isFound = _transcriptsDictionary.TryGetValue(meetingId, out string transcripts);
                if (isFound)
                {
                    ViewBag.Transcripts = $"Format: {transcripts}";
                }
                else
                {
                    var result = await _graphHelper.GetMeetingTranscriptionsAsync(meetingId);
                    if (!string.IsNullOrEmpty(meetingId))
                    {
                        _transcriptsDictionary.AddOrUpdate(meetingId, result, (key, newValue) => result);
                        ViewBag.Transcripts = $"Format: {result}";
                    }
                }
            }
            return View();
        }
    }
}