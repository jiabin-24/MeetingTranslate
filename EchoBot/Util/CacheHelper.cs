using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;
using StackExchange.Redis;

namespace EchoBot.Util
{
    public class CacheHelper
    {
        public IConnectionMultiplexer Mux { get; private set; }

        private readonly IMemoryCache _memoryCache;

        public CacheHelper(IConnectionMultiplexer mux, IMemoryCache memoryCache)
        {
            Mux = mux;
            _memoryCache = memoryCache;
        }

        public async Task<T> GetOrSetAsync<T>(string key, TimeSpan timeSpan, Func<T> func)
        {
            if (await Mux.GetDatabase().KeyExistsAsync(key))
            {
                var cacheObj = (string)await Mux.GetDatabase().StringGetAsync(key);
                return JsonConvert.DeserializeObject<T>(cacheObj)!;
            }

            var obj = func();
            await Mux.GetDatabase().StringSetAsync(key, JsonConvert.SerializeObject(obj), timeSpan);
            return obj;
        }

        public async Task<T> GetAsync<T>(string key)
        {
            if (await Mux.GetDatabase().KeyExistsAsync(key))
            {
                var cacheObj = (string)await Mux.GetDatabase().StringGetAsync(key);
                return cacheObj == null ? default : JsonConvert.DeserializeObject<T>(cacheObj)!;
            }
            return default;
        }

        public async Task DeleteAsync(string key)
        {
            await Mux.GetDatabase().KeyDeleteAsync(key);
        }

        /// <summary>
        /// 删除某个父 key 下所有以 ":" 分隔的子节点缓存。
        /// 例如 parentKey="user:1"，会删除 "user:1:*"。
        /// </summary>
        /// <param name="parentKey">父 key（不带末尾冒号）</param>
        /// <param name="pageSize">每次扫描批次大小</param>
        /// <param name="includeParent">是否同时删除父 key 本身</param>
        /// <returns>实际删除的 key 数量</returns>
        public async Task<long> DeleteChildrenAsync(string parentKey, int pageSize = 1000, bool includeParent = false)
        {
            if (string.IsNullOrWhiteSpace(parentKey))
                return 0;

            var db = Mux.GetDatabase();
            var dbNumber = db.Database < 0 ? 0 : db.Database;
            var pattern = $"{parentKey}:*";
            long deleted = 0;

            foreach (var endpoint in Mux.GetEndPoints())
            {
                var server = Mux.GetServer(endpoint);
                if (!server.IsConnected)
                    continue;

                // 从库只读，不执行删除
                if (server.IsReplica)
                    continue;

                var slotBatches = new Dictionary<int, List<RedisKey>>();
                foreach (var key in server.Keys(dbNumber, pattern, pageSize: pageSize))
                {
                    var slot = Mux.HashSlot(key);
                    if (!slotBatches.TryGetValue(slot, out var batch))
                    {
                        batch = new List<RedisKey>(pageSize);
                        slotBatches[slot] = batch;
                    }

                    batch.Add(key);
                    if (batch.Count >= pageSize)
                    {
                        deleted += await db.KeyDeleteAsync(batch.ToArray()).ConfigureAwait(false);
                        batch.Clear();
                    }
                }

                foreach (var batch in slotBatches.Values)
                {
                    if (batch.Count <= 0)
                        continue;

                    deleted += await db.KeyDeleteAsync(batch.ToArray()).ConfigureAwait(false);
                    batch.Clear();
                }
            }

            if (includeParent)
            {
                if (await db.KeyDeleteAsync(parentKey).ConfigureAwait(false))
                    deleted++;
            }

            return deleted;
        }

        public async Task SetAsync(string key, TimeSpan timeSpan, object obj)
        {
            await Mux.GetDatabase().StringSetAsync(key, JsonConvert.SerializeObject(obj), timeSpan);
        }

        /// <summary>
        /// 先从内存缓存获取，如果没有则从分布式缓存获取并设置到内存缓存中
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="key"></param>
        /// <param name="memoryTtl"></param>
        /// <returns></returns>
        public async Task<T> GetWithMemoryCacheAsync<T>(string key, TimeSpan memoryTtl)
        {
            if (_memoryCache.TryGetValue<T>(key, out var cachedValue))
                return cachedValue;

            var value = await GetAsync<T>(key);
            _memoryCache.Set(key, value, memoryTtl);
            return value;
        }
    }
}
