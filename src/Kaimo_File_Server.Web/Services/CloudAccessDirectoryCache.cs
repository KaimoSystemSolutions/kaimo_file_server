using System.Collections.Concurrent;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Microsoft.Extensions.Caching.Memory;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Short-lived, process-local metadata cache for virtual share directory
/// listings. File contents and provider credentials are never cached here.
/// </summary>
public sealed class CloudAccessDirectoryCache
{
    private readonly IMemoryCache _cache;
    private readonly ICloudAccessSettingsStore _settingsStore;
    private readonly ConcurrentDictionary<CacheKey, SemaphoreSlim> _loadLocks = new();
    private readonly ConcurrentDictionary<Guid, long> _shareGenerations = new();
    private long _globalGeneration;

    public CloudAccessDirectoryCache(
        IMemoryCache cache,
        ICloudAccessSettingsStore settingsStore)
    {
        _cache = cache;
        _settingsStore = settingsStore;
        settingsStore.SettingsChanged += InvalidateAll;
    }

    public async Task<TimeSpan> GetTimeToLiveAsync()
        => TimeSpan.FromSeconds((await _settingsStore.GetAsync()).DirectoryCacheSeconds);

    public async Task<List<FileMetadata>> GetOrCreateAsync(
        Guid shareId,
        string relativePath,
        Func<Task<List<FileMetadata>>> factory)
    {
        var key = CreateKey(shareId, relativePath);
        if (_cache.TryGetValue(key, out List<FileMetadata>? cached) && cached is not null)
            return Clone(cached);

        var gate = _loadLocks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (_cache.TryGetValue(key, out cached) && cached is not null)
                return Clone(cached);

            var loaded = await factory();
            var snapshot = Clone(loaded);
            _cache.Set(key, snapshot, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = await GetTimeToLiveAsync()
            });
            return Clone(snapshot);
        }
        finally
        {
            gate.Release();
            _loadLocks.TryRemove(new KeyValuePair<CacheKey, SemaphoreSlim>(key, gate));
        }
    }

    /// <summary>
    /// Invalidates every cached directory for a share in constant time. Old
    /// generation entries remain unreachable and expire naturally after the TTL.
    /// </summary>
    public void InvalidateShare(Guid shareId)
        => _shareGenerations.AddOrUpdate(shareId, 1, static (_, generation) => generation + 1);

    public void InvalidateAll() => Interlocked.Increment(ref _globalGeneration);

    private CacheKey CreateKey(Guid shareId, string relativePath)
        => new(
            Volatile.Read(ref _globalGeneration),
            shareId,
            _shareGenerations.GetOrAdd(shareId, 0),
            ShareRelativePath.Normalize(relativePath).ToUpperInvariant());

    private static List<FileMetadata> Clone(IEnumerable<FileMetadata> items)
        => items.Select(static item => new FileMetadata
        {
            Id = item.Id,
            ShareId = item.ShareId,
            Path = item.Path,
            Name = item.Name,
            Size = item.Size,
            IsDirectory = item.IsDirectory,
            CreatedAt = item.CreatedAt,
            ModifiedAt = item.ModifiedAt,
            LastAccessedAt = item.LastAccessedAt,
            OwnerId = item.OwnerId,
            Acl = [.. item.Acl]
        }).ToList();

    private readonly record struct CacheKey(
        long GlobalGeneration,
        Guid ShareId,
        long ShareGeneration,
        string RelativePath);
}
