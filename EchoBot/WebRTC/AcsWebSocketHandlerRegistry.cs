using System.Collections.Concurrent;

namespace EchoBot.WebRTC
{
    public static class AcsWebSocketHandlerRegistry
    {
        private static readonly ConcurrentDictionary<string, AcsMediaWebSocketHandler> _registry = new();

        public static void Register(string threadId, string targetLang, AcsMediaWebSocketHandler handler)
        {
            if (threadId == null || handler == null) return;
            _registry[GetKey(threadId, targetLang)] = handler;
        }

        public static void Unregister(string threadId, string targetLang)
        {
            if (threadId == null) return;
            _registry.TryRemove(GetKey(threadId, targetLang), out _);
        }

        public static List<AcsMediaWebSocketHandler> UnregisterByThreadId(string threadId)
        {
            if (threadId == null) return [];
            var keysToRemove = _registry.Keys.Where(k => k.StartsWith($"{threadId}_")).ToList();
            var removedHandlers = new List<AcsMediaWebSocketHandler>();
            foreach (var key in keysToRemove)
            {
                if (_registry.TryRemove(key, out var Handler))
                {
                    removedHandlers.Add(Handler);
                }
            }
            return removedHandlers;
        }

        public static bool TryGet(string threadId, string targetLang, out AcsMediaWebSocketHandler? handler)
        {
            if (threadId == null) { handler = null; return false; }
            return _registry.TryGetValue(GetKey(threadId, targetLang), out handler);
        }

        private static string GetKey(string threadId, string targetLang) => $"{threadId}_{targetLang}";
    }
}
