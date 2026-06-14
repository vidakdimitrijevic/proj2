public class SizeLimitedCache
{
    private class CacheEntry
    {
        public CacheEntry(string value, DateTimeOffset expiresAt)
        {
            Value = value;
            ExpiresAt = expiresAt;
        }

        public string Value { get; }

        public DateTimeOffset ExpiresAt { get; }
    }

    private readonly Dictionary<string, CacheEntry> cache = new();
    private readonly Queue<string> insertionOrder = new();
    private readonly object cacheLock = new();

    private readonly int maxSize;
    private readonly TimeSpan ttl;

    public SizeLimitedCache(int maxSize, TimeSpan ttl)
    {
        if (maxSize <= 0)
        {
            throw new ArgumentException("Velicina kesa mora biti veca od nule.", nameof(maxSize));
        }

        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentException("TTL mora biti veci od nule.", nameof(ttl));
        }

        this.maxSize = maxSize;
        this.ttl = ttl;
    }

    public string? Get(string key)
    {
        lock (cacheLock)
        {
            RemoveExpiredItems();

            if (!cache.ContainsKey(key))
            {
                return null;
            }

            CacheEntry entry = cache[key];

            if (entry.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                cache.Remove(key);
                ThreadSafeLogger.Info($"[CACHE EXPIRED] {key}");
                return null;
            }

            return entry.Value;
        }
    }

    public void Put(string key, string value)
    {
        lock (cacheLock)
        {
            RemoveExpiredItems();

            DateTimeOffset expiresAt = DateTimeOffset.UtcNow.Add(ttl);

            if (cache.ContainsKey(key))
            {
                cache[key] = new CacheEntry(value, expiresAt);
                return;
            }

            while (cache.Count >= maxSize && insertionOrder.Count > 0)
            {
                string oldestKey = insertionOrder.Dequeue();

                if (cache.Remove(oldestKey))
                {
                    ThreadSafeLogger.Info($"[CACHE REMOVE] {oldestKey}");
                    break;
                }
            }

            cache[key] = new CacheEntry(value, expiresAt);
            insertionOrder.Enqueue(key);
        }
    }

    private void RemoveExpiredItems()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        List<string> expiredKeys = cache
            .Where(pair => pair.Value.ExpiresAt <= now)
            .Select(pair => pair.Key)
            .ToList();

        foreach (string expiredKey in expiredKeys)
        {
            cache.Remove(expiredKey);
            ThreadSafeLogger.Info($"[CACHE EXPIRED] {expiredKey}");
        }
    }
}
