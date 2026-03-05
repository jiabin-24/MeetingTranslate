using System.Collections.Concurrent;

namespace EchoBot.WebRTC
{
    public static class AcsWebSocketHandlerRegistry
    {
        private static readonly ConcurrentDictionary<string, AcsMediaWebSocketHandler> _map = new();

        public static bool Register(string threadId, AcsMediaWebSocketHandler handler)
        {
            if (threadId == null || handler == null) return false;
            return _map.TryAdd(threadId, handler);
        }

        public static bool Unregister(string threadId)
        {
            if (threadId == null) return false;
            return _map.TryRemove(threadId, out _);
        }

        public static bool TryGet(string threadId, out AcsMediaWebSocketHandler? handler)
        {
            if (threadId == null) { handler = null; return false; }
            return _map.TryGetValue(threadId, out handler);
        }

        public static IEnumerable<string> GetAllThreadIds() => _map.Keys;
    }
}
