using System.Collections.Concurrent;
using Kaimo_File_Server.Core.Helpers;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Process-local, TTL-bounded cache of computed directory sizes, keyed by
/// (share, share-relative path). Directory sizing walks the whole subtree at 4×
/// parallelism on every navigation and refresh; without memoisation the same walk
/// re-runs constantly. This caches the result for <see cref="DefaultTtl"/>.
///
/// <para>Accepted staleness: a size can lag reality by up to the TTL after a write the
/// Web process cannot observe (e.g. an SMB-side change). The number was already advisory
/// and already racy, so a short bounded lag is an acceptable trade for not re-walking.</para>
///
/// <para>The cache is itself bounded (<see cref="DefaultMaxEntries"/>) with oldest-first
/// eviction, so it can never become an unbounded-growth defect of its own.</para>
/// </summary>
public sealed class DirectorySizeCache
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);
    private const int DefaultMaxEntries = 50_000;

    private readonly TimeProvider _timeProvider;
    private readonly int _maxEntries;
    private readonly ConcurrentDictionary<Key, Entry> _entries = new();
    private readonly object _evictLock = new();
    private long _sequence;

    public DirectorySizeCache(TimeProvider timeProvider)
        : this(timeProvider, DefaultMaxEntries)
    {
    }

    // Test seam: lets a unit test exercise eviction without inserting 50k entries.
    internal DirectorySizeCache(TimeProvider timeProvider, int maxEntries)
    {
        _timeProvider = timeProvider;
        _maxEntries = maxEntries;
    }

    /// <summary>Returns a cached, non-expired size for the path, or <c>false</c>.</summary>
    public bool TryGet(Guid shareId, string relativePath, out long size)
    {
        var key = MakeKey(shareId, relativePath);
        if (_entries.TryGetValue(key, out var entry))
        {
            if (_timeProvider.GetUtcNow() < entry.ExpiresAt)
            {
                size = entry.Size;
                return true;
            }
            // Expired: drop it so it cannot linger past the TTL.
            _entries.TryRemove(key, out _);
        }

        size = 0;
        return false;
    }

    /// <summary>Caches <paramref name="size"/> for the path for <see cref="DefaultTtl"/>.</summary>
    public void Set(Guid shareId, string relativePath, long size)
    {
        var seq = Interlocked.Increment(ref _sequence);
        _entries[MakeKey(shareId, relativePath)] =
            new Entry(size, _timeProvider.GetUtcNow() + DefaultTtl, seq);
        EvictIfOverCapacity();
    }

    /// <summary>
    /// Drops the entry for <paramref name="relativePath"/> and every ancestor, so a write
    /// inside a folder invalidates the cached size of the whole chain up to the share root.
    /// </summary>
    public void Invalidate(Guid shareId, string relativePath)
    {
        foreach (var ancestor in ShareRelativePath.BuildHierarchy(relativePath))
            _entries.TryRemove(MakeKey(shareId, ancestor), out _);
    }

    /// <summary>Drops every cached size for a share (e.g. on share move or delete).</summary>
    public void InvalidateShare(Guid shareId)
    {
        foreach (var key in _entries.Keys.Where(k => k.ShareId == shareId).ToList())
            _entries.TryRemove(key, out _);
    }

    private void EvictIfOverCapacity()
    {
        if (_entries.Count <= _maxEntries) return;
        lock (_evictLock)
        {
            var overflow = _entries.Count - _maxEntries;
            if (overflow <= 0) return;
            var oldest = _entries
                .OrderBy(kv => kv.Value.Sequence)
                .Take(overflow)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var key in oldest)
                _entries.TryRemove(key, out _);
        }
    }

    private static Key MakeKey(Guid shareId, string relativePath)
        => new(shareId, ShareRelativePath.Normalize(relativePath).ToUpperInvariant());

    private readonly record struct Key(Guid ShareId, string Path);

    private readonly record struct Entry(long Size, DateTimeOffset ExpiresAt, long Sequence);
}
