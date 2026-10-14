using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Infrastructure.Persistence;
using Kaimo_File_Server.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Services;

/// <summary>
/// Enforces the recycle-bin limits of every enabled share with an enabled bin: retention
/// (<see cref="ShareDefinition.RecycleRetentionDays"/>) and maximum size
/// (<see cref="ShareDefinition.GetEffectiveRecycleMaxSizeBytes"/>, per recycle root, so every
/// home bin of the home-folder share is limited on its own). Works on disk only, so items
/// deleted over Web, REST, WebDAV and SMB are treated alike.
///
/// The deletion time is the first run that saw an item (<see cref="RecycleBinEntry"/>), never
/// the file's own timestamps: a folder moved into the bin keeps its children's old times, and
/// bin folders get new ones on every change. An item is therefore purged at most one run late
/// and never early. A changed inode change time means the item was replaced (restore and
/// delete again) and restarts its clock.
///
/// Purge order: oldest deletion first. Folders are removed once a purge emptied them, or when
/// they are empty and older than the retention themselves; the bin root always stays.
/// ponytail: one tracking row per bin entry and a full scan per run; bins with millions of
/// entries would need batched scans.
/// </summary>
public sealed class RecycleBinJanitor(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IConfigRepository config,
    IAclRepository acls,
    IFileVersionService versions,
    IFileChangeLogRepository changeLog,
    ILogger<RecycleBinJanitor> logger,
    TimeProvider? time = null)
{
    /// <summary>Global default size limit in bytes per recycle bin; 0 or absent = unlimited.</summary>
    public const string GlobalMaxSizeConfigKey = "recycle.maxSizeBytes";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private readonly record struct ScannedEntry(string BinRoot, bool IsDirectory, long CtimeTicks, long SizeBytes);

    public async Task RunOnceAsync(CancellationToken ct)
    {
        var globalMax = await config.GetAsync(GlobalMaxSizeConfigKey, 0L);
        List<ShareDefinition> shares;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            shares = await db.ShareDefinitions.AsNoTracking()
                .Where(s => s.IsEnabled && s.IsRecycleEnabled)
                .ToListAsync(ct);

        foreach (var share in shares)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await CleanShareAsync(share, globalMax, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not ReadOnlyDemoException)
            {
                logger.LogWarning(ex, "Recycle bin of share '{ShareName}' could not be cleaned up", share.Name);
            }
        }
    }

    private async Task CleanShareAsync(ShareDefinition share, long globalMax, CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var scanned = Scan(share);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.RecycleBinEntries
            .Where(e => e.ShareId == share.Id)
            .ToDictionaryAsync(e => e.Path, StringComparer.Ordinal, ct);

        foreach (var (path, row) in rows)
            if (!scanned.ContainsKey(path))
                db.RecycleBinEntries.Remove(row);
        foreach (var (path, item) in scanned)
        {
            if (!rows.TryGetValue(path, out var row))
            {
                rows[path] = row = new RecycleBinEntry { ShareId = share.Id, Path = path, FirstSeenUtc = now };
                db.RecycleBinEntries.Add(row);
            }
            else if (row.CtimeTicks != item.CtimeTicks)
            {
                row.FirstSeenUtc = now;
            }
            row.CtimeTicks = item.CtimeTicks;
            row.SizeBytes = item.SizeBytes;
        }
        await db.SaveChangesAsync(ct);

        DateTime? cutoff = share.RecycleRetentionDays is { } days ? now.AddDays(-days) : null;
        var limit = share.GetEffectiveRecycleMaxSizeBytes(globalMax);
        if (cutoff is null && limit is null)
            return;

        int purgedCount = 0;
        long purgedBytes = 0;
        foreach (var bin in scanned.GroupBy(e => e.Value.BinRoot))
        {
            var files = bin.Where(e => !e.Value.IsDirectory)
                .OrderBy(e => rows[e.Key].FirstSeenUtc)
                .ThenBy(e => e.Key, StringComparer.Ordinal)
                .ToList();

            var victims = files.Where(e => rows[e.Key].FirstSeenUtc < cutoff).ToList();
            if (limit is { } max)
            {
                var chosen = victims.Select(v => v.Key).ToHashSet(StringComparer.Ordinal);
                var total = files.Where(f => !chosen.Contains(f.Key)).Sum(f => f.Value.SizeBytes);
                foreach (var file in files.Where(f => !chosen.Contains(f.Key)))
                {
                    if (total <= max)
                        break;
                    victims.Add(file);
                    total -= file.Value.SizeBytes;
                }
            }

            var emptied = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (path, item) in victims)
            {
                if (!await TryPurgeAsync(share, path, isDirectory: false, item.CtimeTicks, ct))
                    continue;
                db.RecycleBinEntries.Remove(rows[path]);
                emptied.Add(ShareRelativePath.GetParent(path));
                purgedCount++;
                purgedBytes += item.SizeBytes;
            }

            // Deepest first, so a parent sees its children already removed.
            var folders = bin.Where(e => e.Value.IsDirectory)
                .Select(e => e.Key)
                .OrderByDescending(p => p.Count(c => c == '/'))
                .ThenBy(p => p, StringComparer.Ordinal);
            foreach (var path in folders)
            {
                if (!emptied.Contains(path) && !(rows[path].FirstSeenUtc < cutoff))
                    continue;
                if (!await TryPurgeAsync(share, path, isDirectory: true, expectedCtimeTicks: null, ct))
                    continue;
                db.RecycleBinEntries.Remove(rows[path]);
                emptied.Add(ShareRelativePath.GetParent(path));
            }
        }
        await db.SaveChangesAsync(ct);

        if (purgedCount > 0)
            logger.LogInformation(
                "Recycle bin of share '{ShareName}': purged {Count} items ({Bytes} bytes)",
                share.Name, purgedCount, purgedBytes);
    }

    private async Task<bool> TryPurgeAsync(
        ShareDefinition share, string path, bool isDirectory, long? expectedCtimeTicks, CancellationToken ct)
    {
        try
        {
            return await PurgeEntryAsync(share, path, isDirectory, expectedCtimeTicks, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ReadOnlyDemoException)
        {
            logger.LogWarning(ex, "Could not purge '{Path}' from the recycle bin of share '{ShareName}'", path, share.Name);
            return false;
        }
    }

    /// <summary>
    /// Permanently removes one bin entry and everything tied to it, in the same order as
    /// <see cref="HomeDirectoryService.DeleteHomeAsync"/>. Skips it (returns <c>false</c>) when it
    /// is gone, was replaced since the scan (<paramref name="expectedCtimeTicks"/>), or is a
    /// folder that is no longer empty.
    /// </summary>
    internal async Task<bool> PurgeEntryAsync(
        ShareDefinition share, string path, bool isDirectory, long? expectedCtimeTicks, CancellationToken ct = default)
    {
        var full = Path.Combine(share.Path, path);
        if (FileLinkStatus.TryGet(full) is not { } status
            || status.IsDirectory != isDirectory
            || expectedCtimeTicks is { } expected && status.CtimeTicks != expected)
            return false;

        try
        {
            // Non-recursive: refuses a folder that is not empty (any more).
            if (isDirectory || status.IsSymlink && OperatingSystem.IsWindows() && Directory.Exists(full))
                Directory.Delete(full);
            else
                File.Delete(full);
        }
        catch (IOException) when (isDirectory)
        {
            return false;
        }

        await acls.DeleteFileMetadataPathsAsync(share.Id, path);
        if (!isDirectory)
            await versions.DeletePathAsync(share.Id, path);
        await changeLog.AppendAsync(new FileChangeLogEntry
        {
            ShareId = share.Id,
            ChangeType = FileChangeType.Deleted,
            Path = path,
            IsDirectory = isDirectory,
            CreatedAtUtc = _time.GetUtcNow().UtcDateTime,
        }, ct);
        return true;
    }

    private Dictionary<string, ScannedEntry> Scan(ShareDefinition share)
    {
        var result = new Dictionary<string, ScannedEntry>(StringComparer.Ordinal);
        foreach (var binRoot in BinRoots(share))
        {
            // A missing bin, or one replaced by a link, has nothing to clean up.
            if (FileLinkStatus.TryGet(Path.Combine(share.Path, binRoot)) is { IsDirectory: true, IsSymlink: false } root)
                ScanFolder(share, binRoot, binRoot, root.Device, result);
        }
        return result;
    }

    /// <summary>Share-relative recycle roots: one per share, or one per home (depth 1).</summary>
    private static IEnumerable<string> BinRoots(ShareDefinition share)
    {
        if (share.RecycleRootDepth == 0)
        {
            yield return ShareEntryPolicy.RecycleBinName;
            yield break;
        }

        foreach (var dir in Directory.EnumerateDirectories(share.Path))
        {
            var name = Path.GetFileName(dir);
            if (!name.StartsWith('.') && FileLinkStatus.TryGet(dir) is { IsDirectory: true, IsSymlink: false })
                yield return name + "/" + ShareEntryPolicy.RecycleBinName;
        }
    }

    private void ScanFolder(
        ShareDefinition share, string folder, string binRoot, ulong device, Dictionary<string, ScannedEntry> result)
    {
        List<string> children;
        try
        {
            children = Directory.EnumerateFileSystemEntries(Path.Combine(share.Path, folder)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not scan '{Path}' in the recycle bin of share '{ShareName}'", folder, share.Name);
            return;
        }

        foreach (var child in children)
        {
            // Links are entries of their own and never followed; other file systems are skipped.
            if (FileLinkStatus.TryGet(child) is not { } status || status.IsDirectory && status.Device != device)
                continue;
            var path = folder + "/" + Path.GetFileName(child);
            result[path] = new ScannedEntry(binRoot, status.IsDirectory, status.CtimeTicks, status.SizeBytes);
            if (status.IsDirectory)
                ScanFolder(share, path, binRoot, device, result);
        }
    }
}

/// <summary>
/// Runs <see cref="RecycleBinJanitor"/> hourly. Registered in the Web host only (single owner);
/// does nothing in the read-only demo.
/// </summary>
public sealed class RecycleBinJanitorService(
    IServiceScopeFactory scopeFactory,
    DemoModeOptions demoMode,
    ILogger<RecycleBinJanitorService> logger) : BackgroundService
{
    private static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (demoMode.ReadOnly)
            return;

        try
        {
            await Task.Delay(StartDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<RecycleBinJanitor>().RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Recycle-bin cleanup failed");
                }
                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
