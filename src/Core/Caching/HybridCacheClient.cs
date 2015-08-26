﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Logging;

namespace Foundatio.Caching {
    public class HybridCacheClient : ICacheClient {
        private readonly string _cacheId = Guid.NewGuid().ToString("N");
        private readonly ICacheClient _distributedCache;
        private readonly InMemoryCacheClient _localCache = new InMemoryCacheClient();
        private readonly IMessageBus _messageBus;
        private long _localCacheHits;
        private long _invalidateCacheCalls;

        public HybridCacheClient(ICacheClient distributedCacheClient, IMessageBus messageBus) {
            _distributedCache = distributedCacheClient;
            _localCache.MaxItems = 100;
            _messageBus = messageBus;
            _messageBus.Subscribe<InvalidateCache>(async cache => await OnMessageAsync(cache));
            _localCache.ItemExpired += (sender, key) => {
                _messageBus.Publish(new InvalidateCache { CacheId = _cacheId, Keys = new[] { key } });
                Logger.Trace().Message("Item expired event: key={0}", key).Write();
            };
        }

        public InMemoryCacheClient LocalCache { get { return _localCache; } }
        public long LocalCacheHits { get { return _localCacheHits; } }
        public long InvalidateCacheCalls { get { return _invalidateCacheCalls; } }

        public int LocalCacheSize {
            get { return _localCache.MaxItems ?? -1; }
            set { _localCache.MaxItems = value; }
        }

        private async Task OnMessageAsync(InvalidateCache message) {
            if (!String.IsNullOrEmpty(message.CacheId) && String.Equals(_cacheId, message.CacheId))
                return;

            Logger.Trace().Message("Invalidating local cache from remote: id={0} keys={1}", message.CacheId, String.Join(",", message.Keys ?? new string[] { })).Write();
            Interlocked.Increment(ref _invalidateCacheCalls);
            if (message.FlushAll)
                await _localCache.RemoveAllAsync();
            else if (message.Keys != null && message.Keys.Length > 0) {
                foreach (var pattern in message.Keys.Where(k => k.EndsWith("*")))
                    await _localCache.RemoveByPrefixAsync(pattern.Substring(0, pattern.Length - 1));

                await _localCache.RemoveAllAsync(message.Keys.Where(k => !k.EndsWith("*")));
            } else
                Logger.Warn().Message("Unknown invalidate cache message").Write();
        }
        
        public async Task<int> RemoveAllAsync(IEnumerable<string> keys = null) {
            _messageBus.Publish(new InvalidateCache { CacheId = _cacheId, FlushAll = keys == null, Keys = keys.ToArray() });
            await _localCache.RemoveAllAsync(keys);
            return await _distributedCache.RemoveAllAsync(keys);
        }

        public async Task RemoveByPrefixAsync(string prefix) {
            _messageBus.Publish(new InvalidateCache { CacheId = _cacheId, Keys = new[] { prefix + "*" } });
            await _localCache.RemoveByPrefixAsync(prefix);
            await _distributedCache.RemoveByPrefixAsync(prefix);
        }

        public async Task<bool> TryGetAsync<T>(string key, out T value) {
            if (await _localCache.TryGetAsync(key, out value)) {
                Logger.Trace().Message("Local cache hit: {0}", key).Write();
                Interlocked.Increment(ref _localCacheHits);
                return true;
            }

            if (await _distributedCache.TryGetAsync(key, out value)) {
                await _localCache.SetAsync(key, value);
                return true;
            }

            return false;
        }

        public Task<IDictionary<string, object>> GetAllAsync(IEnumerable<string> keys) {
            return _distributedCache.GetAllAsync(keys);
        }

        public async Task<bool> AddAsync<T>(string key, T value, TimeSpan? expiresIn = null) {
            await _localCache.AddAsync(key, value, expiresIn);
            return await _distributedCache.AddAsync(key, value, expiresIn);
        }

        public async Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiresIn = null) {
            _messageBus.Publish(new InvalidateCache { CacheId = _cacheId, Keys = new[] { key } });
            await _localCache.SetAsync(key, value, expiresIn);
            return await _distributedCache.SetAsync(key, value, expiresIn);
        }

        public async Task<int> SetAllAsync(IDictionary<string, object> values, TimeSpan? expiresIn = null) {
            if (values == null)
                return 0;

            _messageBus.Publish(new InvalidateCache { CacheId = _cacheId, Keys = values.Keys.ToArray() });
            await _localCache.SetAllAsync(values);
            return await _distributedCache.SetAllAsync(values);
        }

        public async Task<bool> ReplaceAsync<T>(string key, T value, TimeSpan? expiresIn = null) {
            _messageBus.Publish(new InvalidateCache { CacheId = _cacheId, Keys = new[] { key } });
            await _localCache.ReplaceAsync(key, value, expiresIn);
            return await _distributedCache.ReplaceAsync(key, value, expiresIn);
        }

        public Task<long> IncrementAsync(string key, int amount = 1, TimeSpan? expiresIn = null) {
            return _distributedCache.IncrementAsync(key, amount);
        }

        public Task<TimeSpan?> GetExpirationAsync(string key) {
            return _distributedCache.GetExpirationAsync(key);
        }

        public async Task SetExpirationAsync(string key, TimeSpan expiresIn) {
            _messageBus.Publish(new InvalidateCache { CacheId = _cacheId, Keys = new[] { key } });
            await _localCache.RemoveAsync(key);
            await _distributedCache.SetExpirationAsync(key, expiresIn);
        }

        public void Dispose() { }

        public class InvalidateCache {
            public string CacheId { get; set; }
            public string[] Keys { get; set; }
            public bool FlushAll { get; set; }
        }
        
    }
}
