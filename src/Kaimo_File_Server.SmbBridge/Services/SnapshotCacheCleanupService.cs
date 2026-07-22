using Kaimo_File_Server.Core.Repositories;

namespace Kaimo_File_Server.SmbBridge.Services;

/// <summary>
/// Background evictor for the isolated snapshot materialization cache
/// (<c>&lt;cache-root&gt;/&lt;share-id&gt;/&lt;@GMT&gt;/&lt;user-id&gt;/…</c>) written by
/// <see cref="SnapshotGrpcService"/>. Without it the cache grows unbounded — nothing
/// removed entries by age or when a share/version was deleted (backlog item A.3).
///
/// Each sweep, per share:
///   1. <b>TTL eviction</b> — delete @GMT token directories older than
///      <c>Snapshots:Cache:TtlHours</c> (age from <see cref="SnapshotCache.CachedAtUtc"/>,
///      i.e. the real materialization time, not the historical file mtimes).
///   2. <b>Size cap</b> — if a share's cache still exceeds
///      <c>Snapshots:Cache:MaxBytesPerShare</c>, delete oldest token directories first
///      until it fits.
/// It also removes legacy in-share caches and cache roots belonging to deleted shares.
///
/// All eviction is safe: the cache is a rebuildable, content-addressed projection of
/// <see cref="IFileVersionService"/>; a re-requested version is simply re-materialized.
/// </summary>
public sealed class SnapshotCacheCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly ILogger<SnapshotCacheCleanupService> _logger;
    private readonly string _cacheRoot;

    public SnapshotCacheCleanupService(
        IServiceScopeFactory scopes,
        IConfiguration config,
        ILogger<SnapshotCacheCleanupService> logger)
    {
        _scopes = scopes;
        _config = config;
        _cacheRoot = SnapshotCache.ConfiguredRoot(config);
        _logger = logger;
    }

    private TimeSpan Ttl =>
        TimeSpan.FromHours(_config.GetValue("Snapshots:Cache:TtlHours", 24.0));

    private long MaxBytesPerShare =>
        _config.GetValue("Snapshots:Cache:MaxBytesPerShare", 5L * 1024 * 1024 * 1024); // 5 GiB

    private TimeSpan SweepInterval =>
        TimeSpan.FromMinutes(_config.GetValue("Snapshots:Cache:SweepMinutes", 30.0));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small initial delay so startup (DB migrations, first share sync) settles.
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Snapshot cache sweep failed; retrying next interval.");
            }

            try { await Task.Delay(SweepInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SweepOnceAsync(CancellationToken ct)
    {
        List<Core.Domain.ShareDefinition> shares;
        using (var scope = _scopes.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IShareRepository>();
            shares = await repo.GetAllAsync();
        }

        TimeSpan ttl = Ttl;
        long cap = MaxBytesPerShare;
        DateTime cutoff = DateTime.UtcNow - ttl;

        int evictedTtl = 0, evictedSize = 0, evictedLegacy = 0, evictedOrphan = 0;
        var activeShareIds = shares.Select(s => s.Id.ToString("N"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var share in shares)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(share.Path)) continue;

            // P0-06 upgrade cleanup: old releases wrote decompressed historical
            // content below the client-visible share. The VFS denies this reserved
            // namespace immediately; the sweeper removes the stale bytes as well.
            string legacyRoot = Path.Combine(
                share.Path, SnapshotCache.LegacyDirName);
            if (LooksLikeManagedLegacyCache(legacyRoot) && TryDeleteDir(legacyRoot))
                evictedLegacy++;

            try
            {
                SnapshotCache.EnsureIsolatedFromShare(_cacheRoot, share.Path);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex,
                    "Snapshot cache overlaps share {Share}; skipping cache sweep for it.",
                    share.Name);
                continue;
            }

            string root = SnapshotCache.ShareRootFor(_cacheRoot, share.Id);
            if (!Directory.Exists(root)) continue;

            // Token dirs are the immediate children of a share-id cache root.
            var tokens = new List<(string dir, DateTime cachedAt, long size)>();
            foreach (var dir in SafeEnumerateDirectories(root))
            {
                DateTime cachedAt = SnapshotCache.CachedAtUtc(dir);
                if (cachedAt < cutoff)
                {
                    if (TryDeleteDir(dir)) evictedTtl++;
                    continue;
                }
                tokens.Add((dir, cachedAt, DirectorySize(dir)));
            }

            // Size cap: evict oldest-first until under the per-share limit.
            long total = tokens.Sum(t => t.size);
            if (total > cap)
            {
                foreach (var t in tokens.OrderBy(t => t.cachedAt))
                {
                    if (total <= cap) break;
                    if (TryDeleteDir(t.dir))
                    {
                        total -= t.size;
                        evictedSize++;
                    }
                }
            }
        }

        // The cache no longer lives inside a share tree, so explicitly remove
        // projections whose share definition has been deleted.
        if (Directory.Exists(_cacheRoot))
        {
            foreach (string root in SafeEnumerateDirectories(_cacheRoot))
            {
                ct.ThrowIfCancellationRequested();
                if (!activeShareIds.Contains(Path.GetFileName(root)) &&
                    TryDeleteDir(root))
                    evictedOrphan++;
            }
        }

        if (evictedTtl > 0 || evictedSize > 0 ||
            evictedLegacy > 0 || evictedOrphan > 0)
            _logger.LogInformation(
                "Snapshot cache sweep: evicted {Ttl} by TTL (> {Hours:0.#}h), {Size} by size cap ({Cap} B/share), {Legacy} legacy roots, {Orphan} orphan share roots.",
                evictedTtl, ttl.TotalHours, evictedSize, cap,
                evictedLegacy, evictedOrphan);
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root)
    {
        try { return Directory.EnumerateDirectories(root); }
        catch { return Array.Empty<string>(); }
    }

    private static bool LooksLikeManagedLegacyCache(string root)
    {
        if (!Directory.Exists(root)) return false;
        try
        {
            return Directory.EnumerateDirectories(root, "@GMT-*")
                .Any(tokenDir => File.Exists(Path.Combine(
                    tokenDir, SnapshotCache.MarkerName)));
        }
        catch
        {
            return false;
        }
    }

    private bool TryDeleteDir(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Snapshot cache: failed to evict {Dir}", dir);
            return false;
        }
    }

    private static long DirectorySize(string dir)
    {
        long sum = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { sum += new FileInfo(f).Length; }
                catch { /* file vanished mid-scan */ }
            }
        }
        catch { /* dir vanished mid-scan */ }
        return sum;
    }
}
