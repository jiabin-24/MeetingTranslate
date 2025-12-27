using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace MeetingTranscription.Controllers
{
    [Route("api/{controller}")]
    public class MeetingController : Controller
    {
        private static TasksService _taskService = new TasksService();

        public MeetingController()
        {

        }

        [Route("getMeetingData")]
        public IActionResult GetMeetingData([FromQuery] string meetingId, [FromQuery] string status)
        {
            var currentMeetingList = new List<TaskInfoModel>();

            if (status == "todo")
            {
                _taskService.ToDoDictionary.TryGetValue(meetingId, out currentMeetingList);
            }

            if (currentMeetingList == null)
            {
                return this.Ok(new List<TaskInfoModel>());
            }
            else
            {
                return this.Ok(currentMeetingList);
            }
        }

        [Route("saveMeetingData")]
        [HttpPost]
        public IActionResult SaveMeetingData([FromQuery] string meetingId, [FromQuery] string status, [FromBody] TaskInfoModel taskInfo)
        {
            var currentMeetingList = new List<TaskInfoModel>();

            if (status == "todo")
            {
                var isPresent = _taskService.ToDoDictionary.TryGetValue(meetingId, out currentMeetingList);
                if (isPresent)
                {
                    currentMeetingList.Add(taskInfo);
                }
                else
                {
                    var newMeetingList = new List<TaskInfoModel> { taskInfo };
                    _taskService.ToDoDictionary.AddOrUpdate(meetingId, newMeetingList, (key, newValue) => newMeetingList);
                }
            }

            return this.Ok(currentMeetingList);
        }
    }

    public class TasksService
    {
        private readonly ConcurrentDictionary<string, List<TaskInfoModel>> todoDictionary
        = new ConcurrentDictionary<string, List<TaskInfoModel>>();

        private readonly ConcurrentDictionary<string, List<TaskInfoModel>> doingDictionary
        = new ConcurrentDictionary<string, List<TaskInfoModel>>();

        private readonly ConcurrentDictionary<string, List<TaskInfoModel>> doneDictionary
        = new ConcurrentDictionary<string, List<TaskInfoModel>>();

        public ConcurrentDictionary<string, List<TaskInfoModel>> ToDoDictionary => todoDictionary;
        public ConcurrentDictionary<string, List<TaskInfoModel>> DoingDictionary => doingDictionary;
        public ConcurrentDictionary<string, List<TaskInfoModel>> DoneDictionary => doneDictionary;

    }

    public class TaskInfoModel
    {
        [Required]
        public string TaskDescription { get; set; }

        [Required]
        public string UserName { get; set; }
    }
}
