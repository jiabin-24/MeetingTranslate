using Newtonsoft.Json;
using StackExchange.Redis;

namespace EchoBot.Util
{
    public class CacheHelper
    {
        private readonly IConnectionMultiplexer _mux;

        public CacheHelper(IConnectionMultiplexer mux)
        {
            _mux = mux;
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
                return JsonConvert.DeserializeObject<T>(cacheObj)!;
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
    }
}
