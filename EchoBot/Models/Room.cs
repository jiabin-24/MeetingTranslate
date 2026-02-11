namespace EchoBot.Models
{
    public class Room
    {
        public string RoomId { get; set; }

        public List<RoomParticipant> Participants { get; set; }
    }

    public class RoomParticipant
    {
        public string UserId { get; set; }

        public string UserToken { get; set; }
    }
}
