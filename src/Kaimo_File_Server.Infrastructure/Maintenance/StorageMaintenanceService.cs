using Kaimo_File_Server.Core.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kaimo_File_Server.Infrastructure.Maintenance;

/// <summary>
/// Host-only background service that removes abandoned filesystem artifacts that would
/// otherwise accumulate forever: per-share upload temporaries and cross-pool move
/// staging directories left behind by interrupted operations. It owns no schema and
/// only ever deletes shape-matched artifacts older than their grace period.
///
/// Structure follows <c>SnapshotCacheCleanupService</c>: a settle delay, a fixed tick,
/// per-step failure isolation, and a short-lived DB scope closed BEFORE the filesystem
/// work. Each sweep step runs on its own independent interval.
/// </summary>
public sealed class StorageMaintenanceService(
    IServiceScopeFactory scopes,
    IOptions<StorageMaintenanceOptions> options,
    ShareTempArtifactSweeper sweeper,
    TimeProvider timeProvider,
    IConfiguration configuration,
    ILogger<StorageMaintenanceService> logger) : BackgroundService
{
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(15);

    private readonly StorageMaintenanceOptions _options = options.Value;
    private DateTimeOffset? _lastTempArtifactRun;
    private DateTimeOffset? _lastMoveStagingRun;
    private DateTimeOffset? _lastVersionRetentionRun;
    private DateTimeOffset? _lastOrphanBlobRun;
    private int _orphanShardCursor;

    // 256 two-hex storage shards ("00".."ff"), matching FileVersionService.HashToPath's
    // first-level directory.
    private static readonly string[] AllShards =
        Enumerable.Range(0, 256).Select(i => i.ToString("x2")).ToArray();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
            return;

        try { await Task.Delay(SettleDelay, timeProvider, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunDueStepsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Storage maintenance sweep failed; retrying next tick.");
            }

            try { await Task.Delay(Tick, timeProvider, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RunDueStepsAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();

        if (_options.TempArtifactSweepEnabled
            && IsStepDue(now, _lastTempArtifactRun, _options.TempArtifactInterval))
        {
            try { await SweepTempArtifactsAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Temp-artifact sweep step failed.");
            }
            _lastTempArtifactRun = now;
        }

        if (_options.MoveStagingSweepEnabled
            && IsStepDue(now, _lastMoveStagingRun, _options.MoveStagingInterval))
        {
            try { SweepMoveStaging(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Move-staging sweep step failed.");
            }
            _lastMoveStagingRun = now;
        }

        if (_options.VersionRetentionSweepEnabled
            && IsStepDue(now, _lastVersionRetentionRun, _options.VersionRetentionInterval))
        {
            var moreWorkPending = false;
            try { moreWorkPending = await SweepExpiredVersionsAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Version-retention sweep step failed.");
            }
            // While a backlog remains (e.g. the first sweep after upgrade), stay due so the
            // next tick keeps draining it; otherwise wait the full interval.
            _lastVersionRetentionRun = moreWorkPending ? null : now;
        }

        if (_options.OrphanBlobSweepEnabled
            && IsStepDue(now, _lastOrphanBlobRun, _options.OrphanBlobInterval))
        {
            try { await ReclaimOrphanBlobsAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Orphan-blob sweep step failed.");
            }
            _lastOrphanBlobRun = now;
        }
    }

    private async Task<bool> SweepExpiredVersionsAsync(CancellationToken ct)
    {
        var maxAgeDays = configuration.GetValue("Versioning:MaxAgeDays", 90);
        var maxAge = TimeSpan.FromDays(maxAgeDays);

        using var scope = scopes.CreateScope();
        var versions = scope.ServiceProvider.GetRequiredService<Core.Services.File.IFileVersionService>();
        var result = await versions.SweepExpiredVersionsAsync(
            maxAge, _options.VersionRetentionMinVersionsToKeep, _options.VersionRetentionMaxPaths, ct);
        return result.MoreWorkPending;
    }

    private async Task ReclaimOrphanBlobsAsync(CancellationToken ct)
    {
        // Rotate through the 256 shards, OrphanBlobShardsPerRun at a time, so a full cycle
        // is deterministic and bounded and never a full-tree scan.
        var count = Math.Min(_options.OrphanBlobShardsPerRun, AllShards.Length);
        var shards = new List<string>(count);
        for (var i = 0; i < count; i++)
            shards.Add(AllShards[(_orphanShardCursor + i) % AllShards.Length]);
        _orphanShardCursor = (_orphanShardCursor + count) % AllShards.Length;

        using var scope = scopes.CreateScope();
        var versions = scope.ServiceProvider.GetRequiredService<Core.Services.File.IFileVersionService>();
        await versions.ReclaimOrphanBlobsAsync(shards, _options.OrphanBlobGrace, ct);
    }

    private async Task SweepTempArtifactsAsync(CancellationToken ct)
    {
        // Short-lived DB scope, closed before the filesystem work begins.
        List<string> shareRoots;
        using (var scope = scopes.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IShareRepository>();
            var shares = await repo.GetAllAsync();
            shareRoots = shares
                .Where(s => !string.IsNullOrEmpty(s.Path))
                .Select(s => s.Path)
                .ToList();
        }

        var total = new ArtifactSweepResult();
        foreach (var root in shareRoots)
        {
            ct.ThrowIfCancellationRequested();
            total += sweeper.SweepShare(root, _options.TempArtifactGrace, ct);
        }

        if (total.Deleted > 0 || total.Failed > 0)
            logger.LogInformation(
                "Storage maintenance removed {Deleted} abandoned upload temporaries across {Shares} shares ({Failed} could not be deleted).",
                total.Deleted, shareRoots.Count, total.Failed);
    }

    private void SweepMoveStaging(CancellationToken ct)
    {
        var total = new ArtifactSweepResult();
        var pools = EnumeratePoolRoots();
        foreach (var pool in pools)
        {
            ct.ThrowIfCancellationRequested();
            total += sweeper.SweepPool(pool, _options.MoveStagingGrace, ct);
        }

        if (total.Deleted > 0 || total.Failed > 0)
            logger.LogInformation(
                "Storage maintenance removed {Deleted} abandoned move-staging directories across {Pools} pools ({Failed} could not be deleted).",
                total.Deleted, pools.Count, total.Failed);
    }

    /// <summary>
    /// Pool roots are re-enumerated each run so a newly added pool is swept without a
    /// Host restart. Mirrors Host/Program's Storage:RootPath resolution.
    /// </summary>
    private List<string> EnumeratePoolRoots()
    {
        var baseStoragePath = configuration.GetValue<string>("Storage:RootPath") ?? "/data/storage";
        try
        {
            return Directory.Exists(baseStoragePath)
                ? Directory.GetDirectories(baseStoragePath).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not enumerate storage pools below {Root}.", baseStoragePath);
            return [];
        }
    }

    /// <summary>Pure decision: is a step due now? Exposed for testing.</summary>
    public static bool IsStepDue(DateTimeOffset now, DateTimeOffset? lastRun, TimeSpan interval)
        => lastRun is null || (now - lastRun.Value) >= interval;
}
