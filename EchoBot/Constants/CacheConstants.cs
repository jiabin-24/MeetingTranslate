namespace EchoBot.Constants
{
    public static class CacheConstants
    {
        public static string BotMeetingsKey(string threadId) => $"bot_meeting_{threadId}";

        public static string AcsConnectKey(string groupId) => $"CallConnectionId_{groupId}";

        public static string AcsRoomKey(string groupId) => $"RoomId_{groupId}";

        public static string AcsRoomParticipantKey(string groupId) => $"RoomParticipantId_{groupId}";
    }
}
