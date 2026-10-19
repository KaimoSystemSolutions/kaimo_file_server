using System.Collections.Concurrent;
using Kaimo_File_Server.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public partial class FileBrowserViewModel
{
    // Written from a background task (bounded parallelism) while the render thread
    // reads it — hence concurrent, not a plain Dictionary, to avoid torn reads.
    public ConcurrentDictionary<string, long> DirectorySizes { get; private set; } = new();
    private CancellationTokenSource? _sizeCts;

    // Directory-size calculation walks the disk per folder; cap the fan-out and
    // coalesce UI refreshes so a folder with many sub-directories doesn't trigger
    // one full re-render per sub-directory.
    private const int MaxConcurrentSizeCalculations = 4;
    private static readonly TimeSpan SizeUpdateThrottle = TimeSpan.FromMilliseconds(150);
    public Dictionary<string, int> AclCounts { get; private set; } = new();

    /// <summary>Load ACL counts for current path + all visible items in one DB call.</summary>
    public async Task LoadAclCountsAsync()
    {
        AclCounts.Clear();

        // Counts are only ever rendered for ACL managers; skip the DB query entirely
        // for everyone else instead of computing data the view will not show.
        if (CurrentShare is null || !CanManageAcls) return;

        var paths = new List<string> { CurrentPath ?? "" };

        foreach (var item in Items)
        {
            var relativePath = item.Path;
            if (relativePath.StartsWith(CurrentShare.Path))
                relativePath = relativePath[CurrentShare.Path.Length..].TrimStart('/');
            paths.Add(relativePath);
        }

        var distinctPaths = paths.Distinct().ToList();

        await using var db = await _dbFactory.CreateDbContextAsync();

        try
        {
            AclCounts = await db.FileMetadata
                .Where(fm => fm.ShareId == CurrentShare.Id && distinctPaths.Contains(fm.Path))
                .Select(fm => new { fm.Path, Count = fm.Acl.Count })
                .ToDictionaryAsync(x => x.Path, x => x.Count);
        }
        catch (Exception ex)
        {
            // FIX: was silently swallowed — now logged so failures are visible
            _logger.LogWarning(ex, "Failed to load ACL counts for share '{ShareName}'",
                CurrentShare.Name);
        }
    }

    public int GetAclCount(string path)
    {
        return AclCounts.TryGetValue(path, out var count) ? count : 0;
    }

    /// <summary>
    /// Computes directory sizes off the render path. Sizes are calculated with bounded
    /// parallelism and pushed to the UI on a throttled cadence (plus one final update),
    /// instead of one full re-render per sub-directory. Any in-flight run is cancelled
    /// first, e.g. when the user quickly switches folders or navigates away.
    /// </summary>
    internal async Task LoadDirectorySizesInBackgroundAsync()
    {
        // Cancel and replace the previous run (fast folder switches).
        _sizeCts?.Cancel();
        _sizeCts?.Dispose();
        _sizeCts = new CancellationTokenSource();
        var ct = _sizeCts.Token;

        DirectorySizes.Clear();

        if (_fileService is null || CurrentShare is null) return;

        var userContext = await GetCurrentUserContextAsync();
        if (userContext is null) return;

        var dirs = Items.Where(f => f.IsDirectory).ToList();
        if (dirs.Count == 0) return;

        // Throttle timestamp shared across worker threads; guarded with Interlocked
        // so the compare-and-set is race-free and DateTime never tears.
        long lastPushTicks = 0;

        try
        {
            await Parallel.ForEachAsync(
                dirs,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = MaxConcurrentSizeCalculations,
                    CancellationToken = ct
                },
                async (dir, token) =>
                {
                    try
                    {
                        var relativePath = dir.Path;
                        if (relativePath.StartsWith(CurrentShare.Path))
                            relativePath = relativePath[CurrentShare.Path.Length..].TrimStart('/');

                        var size = await _fileService.GetDirectorySizeAsync(relativePath, userContext);
                        DirectorySizes[relativePath] = size;

                        PushThrottledStateChange(ref lastPushTicks);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Could not calculate size for '{Path}'", dir.Path);
                    }
                });
        }
        catch (OperationCanceledException)
        {
            return; // superseded by a newer load — drop this run silently
        }

        // Final update so the last batch of results is rendered even if the
        // throttle window swallowed the trailing push.
        if (!ct.IsCancellationRequested)
            OnStateChanged?.Invoke();
    }

    /// <summary>Raises <see cref="OnStateChanged"/> at most once per throttle window.</summary>
    private void PushThrottledStateChange(ref long lastPushTicks)
    {
        var now = DateTime.UtcNow.Ticks;
        var previous = Interlocked.Read(ref lastPushTicks);

        if (now - previous < SizeUpdateThrottle.Ticks)
            return;

        // Only the thread that wins the swap pushes, so bursts collapse to one update.
        if (Interlocked.CompareExchange(ref lastPushTicks, now, previous) == previous)
            OnStateChanged?.Invoke();
    }

    /// <summary>Returns the computed size if it has already been calculated.</summary>
    public long? GetDirectorySize(FileMetadata dir)
    {
        if (!dir.IsDirectory || CurrentShare is null) return dir.Size;

        var relativePath = dir.Path;
        if (relativePath.StartsWith(CurrentShare.Path))
            relativePath = relativePath[CurrentShare.Path.Length..].TrimStart('/');

        return DirectorySizes.TryGetValue(relativePath, out var size) ? size : null;
    }
}
