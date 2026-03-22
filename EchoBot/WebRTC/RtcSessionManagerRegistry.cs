using System.Collections.Concurrent;

namespace EchoBot.WebRTC
{
    public static class RtcSessionManagerRegistry
    {
        private static readonly ConcurrentDictionary<string, RtcSessionManager> _registry = new();

        public static void Register(string threadId, string targetLang, RtcSessionManager manager)
        {
            if (threadId == null || manager == null) return;
            _registry[GetKey(threadId, targetLang)] = manager;
        }

        public static RtcSessionManager Unregister(string threadId, string targetLang)
        {
            if (threadId == null) return null;
            _registry.TryRemove(GetKey(threadId, targetLang), out var manager);
            return manager;
        }

        public static List<RtcSessionManager> UnregisterByThreadId(string threadId)
        {
            if (threadId == null) return [];
            var keysToRemove = _registry.Keys.Where(k => k.StartsWith($"{threadId}_")).ToList();
            var removedManagers = new List<RtcSessionManager>();
            foreach (var key in keysToRemove)
            {
                if (_registry.TryRemove(key, out var manager))
                {
                    removedManagers.Add(manager);
                }
            }
            return removedManagers;
        }

        public static bool TryGet(string threadId, string targetLang, out RtcSessionManager? manager)
        {
            if (threadId == null) { manager = null; return false; }
            return _registry.TryGetValue(GetKey(threadId, targetLang), out manager);
        }

        public static RtcSessionManager TryRegister(string threadId, string targetLang, Func<RtcSessionManager> managerFactory)
        {
            if (threadId == null || managerFactory == null) return null;
            var key = GetKey(threadId, targetLang);
            return _registry.GetOrAdd(key, _ => managerFactory());
        }

        private static string GetKey(string threadId, string targetLang) => $"{threadId}_{targetLang}";
    }
}
