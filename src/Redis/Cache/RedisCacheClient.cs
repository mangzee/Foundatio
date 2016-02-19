﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Serializer;
using StackExchange.Redis;

namespace Foundatio.Caching {
    public sealed class RedisCacheClient : ICacheClient, IHaveSerializer {
        private readonly ConnectionMultiplexer _connectionMultiplexer;
        private readonly ISerializer _serializer;
        private readonly LoadedLuaScript _setIfHigherScript;
        private readonly LoadedLuaScript _setIfLowerScript;
        private readonly LoadedLuaScript _incrByAndExpireScript;
        private readonly LoadedLuaScript _delByWildcardScript;

        public RedisCacheClient(ConnectionMultiplexer connectionMultiplexer, ISerializer serializer = null) {
            _connectionMultiplexer = connectionMultiplexer;
            _serializer = serializer ?? new JsonNetSerializer();
            
            var setIfLower = LuaScript.Prepare(SET_IF_HIGHER);
            var setIfHigher = LuaScript.Prepare(SET_IF_LOWER);
            var incrByAndExpire = LuaScript.Prepare(INCRBY_AND_EXPIRE);
            var delByWildcard = LuaScript.Prepare(DEL_BY_WILDCARD);

            foreach (var endpoint in _connectionMultiplexer.GetEndPoints()) {
                _setIfHigherScript = setIfLower.Load(_connectionMultiplexer.GetServer(endpoint));
                _setIfLowerScript = setIfHigher.Load(_connectionMultiplexer.GetServer(endpoint));
                _incrByAndExpireScript = incrByAndExpire.Load(_connectionMultiplexer.GetServer(endpoint));
                _delByWildcardScript = delByWildcard.Load(_connectionMultiplexer.GetServer(endpoint));
            }
        }

        public async Task<int> RemoveAllAsync(IEnumerable<string> keys = null) {
            if (keys == null) {
                var endpoints = _connectionMultiplexer.GetEndPoints(true);
                if (endpoints.Length == 0)
                    return 0;

                foreach (var endpoint in endpoints) {
                    var server = _connectionMultiplexer.GetServer(endpoint);

                    try {
                        await server.FlushDatabaseAsync().AnyContext();
                        continue;
                    } catch (Exception) {}

                    try {
                        var redisKeys = server.Keys().ToArray();
                        if (redisKeys.Length > 0) {
                            await Database.KeyDeleteAsync(redisKeys).AnyContext();
                        }
                    } catch (Exception) {}
                }
            } else {
                var redisKeys = keys.Where(k => !String.IsNullOrEmpty(k)).Select(k => (RedisKey)k).ToArray();
                if (redisKeys.Length > 0) {
                    await Database.KeyDeleteAsync(redisKeys).AnyContext();
                    return redisKeys.Length;
                }
            }

            return 0;
        }

        public async Task<int> RemoveByPrefixAsync(string prefix) {
            try {
                var result = await Database.ScriptEvaluateAsync(_delByWildcardScript, new { keys = prefix + "*" }).AnyContext();
                return (int)result;
            } catch (RedisServerException) {
                return 0;
            }
        }

        private static readonly RedisValue _nullValue = "@@NULL";

        public async Task<CacheValue<T>> GetAsync<T>(string key) {
            var redisValue = await Database.StringGetAsync(key).AnyContext();
            
            return await RedisValueToCacheValueAsync<T>(redisValue).AnyContext();
        }

        private async Task<CacheValue<T>> RedisValueToCacheValueAsync<T>(RedisValue redisValue) {
            if (!redisValue.HasValue) return CacheValue<T>.NoValue;
            if (redisValue == _nullValue) return CacheValue<T>.Null;

            try {
                T value;
                if (typeof (T) == typeof (Int16) || typeof (T) == typeof (Int32) || typeof (T) == typeof (Int64) ||
                    typeof (T) == typeof (bool) || typeof (T) == typeof (double) || typeof (T) == typeof (string))
                    value = (T) Convert.ChangeType(redisValue, typeof (T));
                else if (typeof (T) == typeof (Int16?) || typeof (T) == typeof (Int32?) || typeof (T) == typeof (Int64?) ||
                         typeof (T) == typeof (bool?) || typeof (T) == typeof (double?))
                    value = redisValue.IsNull
                        ? default(T)
                        : (T) Convert.ChangeType(redisValue, Nullable.GetUnderlyingType(typeof (T)));
                else
                    value = await _serializer.DeserializeAsync<T>(redisValue.ToString()).AnyContext();

                return new CacheValue<T>(value, true);
            } catch (Exception ex) {
                Logger.Error()
                    .Exception(ex)
                    .Message($"Unable to deserialize value \"{redisValue}\" to type {typeof (T).FullName}")
                    .Write();
                return CacheValue<T>.NoValue;
            }
        }

        public async Task<IDictionary<string, CacheValue<T>>> GetAllAsync<T>(IEnumerable<string> keys) {
            var keyArray = keys.ToArray();
            var values = await Database.StringGetAsync(keyArray.Select(k => (RedisKey)k).ToArray()).AnyContext();

            var result = new Dictionary<string, CacheValue<T>>();
            for (int i = 0; i < keyArray.Length; i++)
                result.Add(keyArray[i], await RedisValueToCacheValueAsync<T>(values[i]).AnyContext());

            return result;
        }

        public async Task<bool> AddAsync<T>(string key, T value, TimeSpan? expiresIn = null) {
            if (expiresIn?.Ticks < 0) {
#if DEBUG
                Logger.Trace().Message($"Removing expired key: {key}").Write();
#endif
                await this.RemoveAsync(key).AnyContext();
                return false;
            }

            return await InternalSetAsync(key, value, expiresIn, When.NotExists).AnyContext();
        }

        public Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiresIn = null) {
            return InternalSetAsync(key, value, expiresIn);
        }

        public async Task<double> SetIfHigherAsync(string key, double value, TimeSpan? expiresIn = null) {
            var result = await Database.ScriptEvaluateAsync(_setIfHigherScript, new { key, value, expires = expiresIn?.TotalSeconds }).AnyContext();
            return (double)result;
        }

        public async Task<double> SetIfLowerAsync(string key, double value, TimeSpan? expiresIn = null) {
            var result = await Database.ScriptEvaluateAsync(_setIfLowerScript, new { key, value, expires = expiresIn?.TotalSeconds }).AnyContext();
            return (double)result;
        }

        private async Task<bool> InternalSetAsync<T>(string key, T value, TimeSpan? expiresIn = null, When when = When.Always, CommandFlags flags = CommandFlags.None) {
            if (value == null)
                return await Database.StringSetAsync(key, _nullValue, expiresIn, when, flags).AnyContext();

            if (typeof(T) == typeof(Int16))
                return await Database.StringSetAsync(key, Convert.ToInt16(value), expiresIn, when, flags).AnyContext();
            if (typeof(T) == typeof(Int32))
                return await Database.StringSetAsync(key, Convert.ToInt32(value), expiresIn, when, flags).AnyContext();
            if (typeof(T) == typeof(Int64))
                return await Database.StringSetAsync(key, Convert.ToInt64(value), expiresIn, when, flags).AnyContext();
            if (typeof(T) == typeof(bool))
                return await Database.StringSetAsync(key, Convert.ToBoolean(value), expiresIn, when, flags).AnyContext();
            if (typeof(T) == typeof(string))
                return await Database.StringSetAsync(key, value?.ToString(), expiresIn, when, flags).AnyContext();

            var data = await _serializer.SerializeAsync(value).AnyContext();
            return await Database.StringSetAsync(key, data, expiresIn, when, flags).AnyContext();
        }

        public async Task<int> SetAllAsync<T>(IDictionary<string, T> values, TimeSpan? expiresIn = null) {
            if (values == null)
                return 0;

            var dictionary = new Dictionary<RedisKey, RedisValue>();
            foreach (var value in values)
                dictionary.Add(value.Key, await _serializer.SerializeAsync(value.Value).AnyContext());
            
            await Database.StringSetAsync(dictionary.ToArray()).AnyContext();
            return values.Count;
        }

        public Task<bool> ReplaceAsync<T>(string key, T value, TimeSpan? expiresIn = null) {
            return InternalSetAsync(key, value, expiresIn, When.Exists);
        }

        public async Task<double> IncrementAsync(string key, double amount = 1, TimeSpan? expiresIn = null) {
            if (expiresIn?.Ticks < 0) {
                await this.RemoveAsync(key).AnyContext();
                return -1;
            }

            if (expiresIn.HasValue) {
                var result = await Database.ScriptEvaluateAsync(_incrByAndExpireScript, new { key, value = amount, expires = expiresIn.Value.TotalSeconds }).AnyContext();
                return (long)result;
            }

            return await Database.StringIncrementAsync(key, amount).AnyContext();
        }
        
        public Task<bool> ExistsAsync(string key) {
            return Database.KeyExistsAsync(key);
        }

        public Task<TimeSpan?> GetExpirationAsync(string key) {
            return Database.KeyTimeToLiveAsync(key);
        }

        public Task SetExpirationAsync(string key, TimeSpan expiresIn) {
            if (expiresIn.Ticks < 0)
                return this.RemoveAsync(key);

            return Database.KeyExpireAsync(key, expiresIn);
        }

        private IDatabase Database => _connectionMultiplexer.GetDatabase();

        public void Dispose() {}
        
        ISerializer IHaveSerializer.Serializer => _serializer;

        private const string SET_IF_HIGHER = @"local c = tonumber(redis.call('get', @key))
if c then
  if tonumber(@value) > c then
    redis.call('set', @key, @value)
    if (@expires) then
      redis.call('expire', @key, @expires)
    end
    return tonumber(@value) - c
  else
    return 0
  end
else
  redis.call('set', @key, @value)
  if (@expires) then
    redis.call('expire', @key, @expires)
  end
  return tonumber(@value)
end";

        private const string SET_IF_LOWER = @"local c = tonumber(redis.call('get', @key))
if c then
  if tonumber(@value) > c then
    redis.call('set', @key, @value)
    if (@expires) then
      redis.call('expire', @key, @expires)
    end
    return tonumber(@value) - c
  else
    return 0
  end
else
  redis.call('set', @key, @value)
  if (@expires) then
    redis.call('expire', @key, @expires)
  end
  return tonumber(@value)
end";

        private const string INCRBY_AND_EXPIRE = @"local v = redis.call('incrby', @key, @value)
if v == @value and @expires then
  redis.call('expire', @key, @expires)
end
return v";

        private const string DEL_BY_WILDCARD = @"return redis.call('del', unpack(redis.call('keys', @keys)))";
    }
}