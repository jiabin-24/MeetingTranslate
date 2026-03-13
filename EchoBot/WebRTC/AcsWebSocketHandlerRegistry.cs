using System.Collections.Concurrent;

namespace EchoBot.WebRTC
{
    public static class AcsWebSocketHandlerRegistry
    {
        private static readonly ConcurrentDictionary<string, AcsMediaWebSocketHandler> _registry = new();
        private static readonly ConcurrentDictionary<string, TaskCompletionSource<AcsMediaWebSocketHandler>> _waiters = new();

        public static AcsMediaWebSocketHandler Register(string threadId, string targetLang, AcsMediaWebSocketHandler handler)
        {
            if (threadId == null || handler == null) return null;
            var key = GetKey(threadId, targetLang);
            _registry[key] = handler;

            if (_waiters.TryGetValue(key, out var waiter))
            {
                waiter.TrySetResult(handler);
            }
            return handler;
        }

        public static AcsMediaWebSocketHandler Unregister(string threadId, string targetLang)
        {
            if (threadId == null) return null;
            var key = GetKey(threadId, targetLang);
            _registry.TryRemove(key, out var handler);

            if (_waiters.TryRemove(key, out var waiter))
            {
                waiter.TrySetCanceled();
            }
            return handler;
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

                if (_waiters.TryRemove(key, out var waiter))
                {
                    waiter.TrySetCanceled();
                }
            }
            return removedHandlers;
        }

        public static bool TryGet(string threadId, string targetLang, out AcsMediaWebSocketHandler? handler)
        {
            if (threadId == null) { handler = null; return false; }
            return _registry.TryGetValue(GetKey(threadId, targetLang), out handler);
        }

        public static async Task<AcsMediaWebSocketHandler?> WaitForHandlerAsync(string threadId, string targetLang, TimeSpan timeout, CancellationToken ct = default)
        {
            if (threadId == null)
                return null;

            var key = GetKey(threadId, targetLang);

            if (_registry.TryGetValue(key, out var existing))
                return existing;

            var waiter = _waiters.GetOrAdd(key, _ => new TaskCompletionSource<AcsMediaWebSocketHandler>(TaskCreationOptions.RunContinuationsAsynchronously));

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            try
            {
                return await waiter.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                return _registry.TryGetValue(key, out var handler) ? handler : null;
            }
            finally
            {
                if (_registry.ContainsKey(key))
                    _waiters.TryRemove(key, out _);
            }
        }

        private static string GetKey(string threadId, string targetLang) => $"{threadId}_{targetLang}";
    }
}
