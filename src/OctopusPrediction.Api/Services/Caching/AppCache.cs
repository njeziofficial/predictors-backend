using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;

namespace OctopusPrediction.Api.Services.Caching;

// The kinds of data cached, each emptied as a whole when what it's built from changes (see
// CacheInvalidationInterceptor for which tables feed which region).
public enum CacheRegion
{
    Fixtures,     // weeks and fixtures
    Leaderboard,  // standings: predictions' points, previous points, player names
    Settings,     // predictions lock, registration open/closed
    Access,       // each user's disabled flag, role and back-office permissions
    People,       // the chat directory
}

// In-memory cache for hot, read-mostly data. Correctness comes from region invalidation after
// every save that touches the data; the TTLs are only a safety net. In memory, so it assumes a
// single API instance (as ChatHub's presence does) — more instances would need a shared cache
// or a backplane to carry the invalidations.
public sealed class AppCache(IMemoryCache cache)
{
    private sealed class Region
    {
        public CancellationTokenSource Source = new();
    }

    private readonly ConcurrentDictionary<CacheRegion, Region> _regions = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<object?>>> _loading = new();

    // Returns the cached value, or loads it once however many requests ask at the same moment
    // (right after a goal empties the leaderboard, everyone asks at once).
    public async Task<T> GetOrCreateAsync<T>(string key, CacheRegion? region, TimeSpan ttl, Func<Task<T>> load)
    {
        if (cache.TryGetValue(key, out var hit)) return (T)hit!;

        var loader = _loading.GetOrAdd(key, _ => new Lazy<Task<object?>>(() => LoadAsync(key, region, ttl, load)));
        try
        {
            return (T)(await loader.Value)!;
        }
        finally
        {
            _loading.TryRemove(new KeyValuePair<string, Lazy<Task<object?>>>(key, loader));
        }
    }

    private async Task<object?> LoadAsync<T>(string key, CacheRegion? region, TimeSpan ttl, Func<Task<T>> load)
    {
        // Taken before loading: if the region is invalidated while the query runs, this token is
        // already cancelled when the entry is stored, so the possibly-stale value expires at once
        // instead of lingering until the TTL.
        var token = region is { } r ? Volatile.Read(ref RegionFor(r).Source).Token : (CancellationToken?)null;
        var value = await load();

        var options = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl };
        if (token is { } t) options.AddExpirationToken(new CancellationChangeToken(t));
        cache.Set(key, value, options);
        return value;
    }

    public void Invalidate(IEnumerable<CacheRegion> regions)
    {
        foreach (var region in regions.Distinct())
        {
            // Not disposed: entries may still be registering on the old token, and a plain
            // CancellationTokenSource holds nothing that needs freeing.
            var old = Interlocked.Exchange(ref RegionFor(region).Source, new CancellationTokenSource());
            old.Cancel();
        }
    }

    private Region RegionFor(CacheRegion region) => _regions.GetOrAdd(region, _ => new Region());
}
