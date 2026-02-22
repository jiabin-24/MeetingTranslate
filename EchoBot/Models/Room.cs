namespace EchoBot.Models
{
    public class Room
    {
        public string RoomId { get; set; }

        public List<RoomParticipant> Participants { get; set; }
    }

    public class RoomParticipant
    {
        /// <summary>
        /// Azure Communication Services 参与者 ID，格式为：8:acs:{userId}，其中 userId 是一个 GUID 字符串
        /// </summary>
        public string UserId { get; set; }

        /// <summary>
        /// 用户的 Entra ID
        /// </summary>
        public string EntraId { get; set; }

        public string UserToken { get; set; }

        public string Lang { get; set; }
    }
}
