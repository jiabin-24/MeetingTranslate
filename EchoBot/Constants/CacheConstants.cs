namespace EchoBot.Constants
{
    public static class CacheConstants
    {
        public static string BotJoinLockKey(string threadId) => $"{threadId}:bot_join_lock";

        public static string CallConnectionStateKey(string threadId) => $"{threadId}:CallConnectionState";

        public static string CallParticipantsKey(string threadId, string name) => $"{threadId}:CallParticipants:{name}";

        public static string AcsConnectLockKey(string threadId) => $"{threadId}:CallConnectionLock";

        public static string AcsRoomKey(string threadId) => $"{threadId}:RoomId";

        public static string AcsConnectKey(string threadId) => $"{threadId}:CallConnectionId";

        public static string AcsRoomParticipantKey(string threadId) => $"{threadId}:RoomParticipantId";

        public static string MeetingCaptionKey(string threadId) => $"{threadId}:MeetingCaption";
    }
}
