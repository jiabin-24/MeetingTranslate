using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;
using StackExchange.Redis;

namespace EchoBot.Util
{
    public class CacheHelper
    {
        private readonly IConnectionMultiplexer _mux;
        private readonly IMemoryCache _memoryCache;

        public CacheHelper(IConnectionMultiplexer mux, IMemoryCache memoryCache)
        {
            _mux = mux;
            _memoryCache = memoryCache;
        }

        public async Task<T> GetOrSetAsync<T>(string key, TimeSpan timeSpan, Func<T> func)
        {
            if (await _mux.GetDatabase().KeyExistsAsync(key))
            {
                var cacheObj = (string)await _mux.GetDatabase().StringGetAsync(key);
                return JsonConvert.DeserializeObject<T>(cacheObj)!;
            }

            var obj = func();
            await _mux.GetDatabase().StringSetAsync(key, JsonConvert.SerializeObject(obj), timeSpan);
            return obj;
        }

        public async Task<T> GetAsync<T>(string key)
        {
            if (await _mux.GetDatabase().KeyExistsAsync(key))
            {
                var cacheObj = (string)await _mux.GetDatabase().StringGetAsync(key);
                return cacheObj == null ? default : JsonConvert.DeserializeObject<T>(cacheObj)!;
            }
            return default;
        }

        public async Task DeleteAsync(string key)
        {
            await _mux.GetDatabase().KeyDeleteAsync(key);
        }

        public async Task SetAsync(string key, TimeSpan timeSpan, object obj)
        {
            await _mux.GetDatabase().StringSetAsync(key, JsonConvert.SerializeObject(obj), timeSpan);
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
