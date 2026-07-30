using Kaimo_File_Server.Core.Repositories;
using Microsoft.Extensions.Options;

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
    private readonly SnapshotCacheOptions _options;
    private readonly SnapshotCacheLeaseManager _leases;
    private readonly ILogger<SnapshotCacheCleanupService> _logger;
    private readonly string _cacheRoot;

    public SnapshotCacheCleanupService(
        IServiceScopeFactory scopes,
        IOptions<SnapshotCacheOptions> options,
        SnapshotCacheLeaseManager leases,
        ILogger<SnapshotCacheCleanupService> logger)
    {
        _scopes = scopes;
        _options = options.Value;
        _leases = leases;
        _cacheRoot = Path.GetFullPath(_options.RootPath);
        _logger = logger;
    }

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

            try { await Task.Delay(_options.SweepInterval, stoppingToken); }
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

        TimeSpan ttl = _options.Ttl;
        long cap = _options.MaxBytesPerShare;
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
            string shareScope = SnapshotCache.RelativeShareRootFor(share.Id);

            // Token dirs are the immediate children of a share-id cache root.
            var tokens = new List<(string dir, DateTime cachedAt, long size)>();
            foreach (var dir in SafeEnumerateDirectories(root))
            {
                DateTime cachedAt = SnapshotCache.CachedAtUtc(dir);
                if (cachedAt < cutoff)
                {
                    if (TryDeleteTokenDir(shareScope, dir)) evictedTtl++;
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
                    if (TryDeleteTokenDir(shareScope, t.dir))
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
                string shareScope = Path.GetFileName(root);
                if (!activeShareIds.Contains(shareScope) &&
                    TryDeleteOrphanShareRoot(shareScope, root, ct))
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

    /// <summary>
    /// Returns a stable snapshot of the immediate child directories. Directory
    /// enumeration is lazy, so materialization must remain inside this method's
    /// exception boundary; returning the enumerable would defer I/O failures to
    /// an unprotected caller-side <c>foreach</c>.
    /// </summary>
    internal IReadOnlyList<string> SafeEnumerateDirectories(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root).ToArray();
        }
        catch (DirectoryNotFoundException)
        {
            // A concurrent materializer or eviction may remove the directory
            // between the existence check and enumeration.
            return Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex,
                "Snapshot cache: failed to enumerate directories below {Dir}",
                root);
            return Array.Empty<string>();
        }
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

    internal bool TryDeleteTokenDir(string shareScope, string tokenDir)
    {
        IDisposable? lease = _leases.TryAcquireEviction(
            _cacheRoot, shareScope, Path.GetFileName(tokenDir));
        if (lease is null)
            return false;

        using (lease)
            return TryDeleteDir(tokenDir);
    }

    private bool TryDeleteOrphanShareRoot(
        string shareScope, string root, CancellationToken ct)
    {
        foreach (string tokenDir in SafeEnumerateDirectories(root))
        {
            ct.ThrowIfCancellationRequested();
            if (!TryDeleteTokenDir(shareScope, tokenDir))
                return false;
        }

        try
        {
            Directory.Delete(root, recursive: false);
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (IOException)
        {
            // A concurrent materializer recreated a token after the final
            // per-token lease was released. Leave the share root for next sweep.
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Snapshot cache: failed to evict orphan share root {Dir}", root);
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
