namespace EchoBot.Constants
{
    public static class CacheConstants
    {
        public static string BotJoinLockKey(string threadId) => $"bot_join_lock_{threadId}";

        public static string AcsConnectKey(string groupId) => $"CallConnectionId_{groupId}";

        public static string AcsConnectLockKey(string groupId) => $"CallConnectionLock_{groupId}";

        public static string AcsRoomKey(string groupId) => $"RoomId_{groupId}";

        public static string AcsRoomParticipantKey(string groupId) => $"RoomParticipantId_{groupId}";

        public static string MsAudioParticipantsKey(string threadId, string sourceId) => $"{threadId}-{sourceId}";
    }
}
