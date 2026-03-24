namespace EchoBot.Constants
{
    public static class CacheConstants
    {
        public static string BotJoinLockKey(string threadId) => $"{threadId}:bot_join_lock";

        public static string AcsConnectKey(string threadId) => $"{threadId}:CallConnectionId";

        public static string CallConnectionStateKey(string threadId) => $"{threadId}:CallConnectionState";

        public static string AcsConnectLockKey(string threadId) => $"{threadId}:CallConnectionLock";

        public static string AcsRoomKey(string threadId) => $"{threadId}:RoomId";

        public static string AcsRoomParticipantKey(string threadId) => $"{threadId}:RoomParticipantId";

        public static string MsAudioParticipantsKey(string threadId, string? audioId) => string.IsNullOrEmpty(audioId) ? $"{threadId}:AudioParticipants"
            : $"{threadId}:AudioParticipants:{audioId}";

        public static string MeetingCaptionKey(string threadId) => $"{threadId}:MeetingCaption";
    }
}
