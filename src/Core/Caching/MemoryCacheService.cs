using System.Collections.Concurrent;
using TalosForge.Core.Abstractions;
using TalosForge.Core.Configuration;

namespace TalosForge.Core.Caching;

/// <summary>
/// In-memory TTL cache with explicit short/long/no-cache policies.
/// </summary>
public sealed class MemoryCacheService : ICacheService
{
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _shortTtl;
    private readonly TimeSpan _longTtl;

    public MemoryCacheService(BotOptions options, TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _shortTtl = TimeSpan.FromMilliseconds(options.ShortCacheTtlMs);
        _longTtl = TimeSpan.FromMilliseconds(options.LongCacheTtlMs);
    }

    public void Set<T>(string key, T value, CachePolicy policy)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Cache key cannot be empty.", nameof(key));
        }

        if (policy == CachePolicy.None)
        {
            _entries.TryRemove(key, out _);
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var expiresAt = policy == CachePolicy.ShortLived ? now.Add(_shortTtl) : now.Add(_longTtl);
        _entries[key] = new CacheEntry(value, expiresAt);
    }

    public bool TryGet<T>(string key, out T? value)
    {
        value = default;
        if (!_entries.TryGetValue(key, out var entry))
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow();
        if (entry.ExpiresAt <= now)
        {
            _entries.TryRemove(key, out _);
            return false;
        }

        if (entry.Value is T typed)
        {
            value = typed;
            return true;
        }

        return false;
    }

    public void Invalidate(string key)
    {
        _entries.TryRemove(key, out _);
    }

    public void Clear()
    {
        _entries.Clear();
    }

    private sealed record CacheEntry(object? Value, DateTimeOffset ExpiresAt);
}
