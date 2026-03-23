namespace EchoBot.Constants
{
    public static class CacheConstants
    {
        public static string BotJoinLockKey(string threadId) => $"bot_join_lock:{threadId}";

        public static string AcsConnectKey(string groupId) => $"CallConnectionId:{groupId}";

        public static string AcsConnectLockKey(string groupId) => $"CallConnectionLock:{groupId}";

        public static string AcsRoomKey(string groupId) => $"RoomId:{groupId}";

        public static string AcsRoomParticipantKey(string groupId) => $"RoomParticipantId:{groupId}";

        public static string MsParticipantsKey(string threadId, string speakerId) => $"Participants:{threadId}:{speakerId}";

        public static string MsAudioParticipantsKey(string threadId, string? audioId) => string.IsNullOrEmpty(audioId) ? $"AudioParticipants:{threadId}"
            : $"AudioParticipants:{threadId}:{audioId}";
    }
}
