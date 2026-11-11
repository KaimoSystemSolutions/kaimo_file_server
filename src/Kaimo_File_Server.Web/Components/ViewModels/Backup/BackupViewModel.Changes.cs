using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Infrastructure.Backup.Restic;

namespace Kaimo_File_Server.Web.Components.ViewModels.Backup;

public partial class BackupViewModel : IDisposable
{
    // ══════════════════════════════════════════
    //  Changes of the selected backup point (restore tab, "Changes" view)
    // ══════════════════════════════════════════

    /// <summary>The restore tab shows what changed in <see cref="BackupPoint"/> instead of its content.</summary>
    public bool ShowChanges { get; private set; }

    /// <summary>The comparison of <see cref="BackupPoint"/>; null while loading, after a failure or a cancel.</summary>
    public BackupChangeSet? Changes { get; private set; }
    public bool IsLoadingChanges { get; private set; }

    /// <summary>Older backup points of the same repository the selected one can be compared with (newest first).</summary>
    public IReadOnlyList<BackupPointView> ChangeBaseCandidates
        => BackupPoint is { } point
            ? BackupPoints.Where(p => p.RepositoryId == point.RepositoryId && p.TimeUtc < point.TimeUtc).ToList()
            : [];

    /// <summary>Comparisons kept per circuit; each may hold up to <see cref="BackupCatalogService.MaximumListedChanges"/> paths.</summary>
    public const int MaximumCachedComparisons = 8;

    /// <summary>Comparisons already made in this circuit: two backup points never change.</summary>
    // ponytail: dropped all at once when full, an LRU if users routinely flip between more points
    private readonly Dictionary<(Guid, string, string?), BackupChangeSet> _changesCache = [];

    /// <summary>The base the last comparison asked for (null: the default one); a retry repeats it.</summary>
    private string? _requestedBase;

    /// <summary>Cancels the running diff (cancel button, closed page).</summary>
    private CancellationTokenSource? _changesCts;

    /// <summary>Switches the restore tab between content and changes, loading the opened view.</summary>
    public Task<bool> ShowChangesAsync(bool show) => RunAsync(async actor =>
    {
        ShowChanges = show;
        if (show)
            await LoadChangesCoreAsync(actor, null);
        else
            await BrowsePointAsync(actor, BrowseFolder);
    });

    /// <summary>Compares the selected backup point with another one of the same repository.</summary>
    public Task<bool> SelectChangeBaseAsync(string snapshotId) => RunAsync(actor => LoadChangesCoreAsync(actor, snapshotId));

    /// <summary>Repeats a failed or cancelled comparison against the same base.</summary>
    public Task<bool> RetryChangesAsync() => RunAsync(actor => LoadChangesCoreAsync(actor, _requestedBase));

    /// <summary>Stops the running diff; the view then offers a retry.</summary>
    public void CancelChanges() => _changesCts?.Cancel();

    /// <summary>A closed page must not keep restic running until the diff times out.</summary>
    public void Dispose() => _changesCts?.Cancel();

    /// <summary>
    /// Selects one backup point from the activity log for the changes view without comparing it
    /// yet, so the page can switch to the restore tab first and show the progress there
    /// (<see cref="ShowChangesAsync"/>). False when the actor may not browse the share's backup
    /// points (the link is hidden then; the server checks again).
    /// </summary>
    public Task<bool> OpenChangesAsync(Guid shareId, Guid repositoryId, string snapshotId)
    {
        if (RestoreShares.All(s => s.ShareId != shareId))
            return Task.FromResult(false);
        return RunAsync(async actor =>
        {
            await LoadShareAsync(actor, shareId);
            BackupPoint = BackupPoints.FirstOrDefault(p => p.RepositoryId == repositoryId && p.SnapshotId == snapshotId)
                          ?? throw new ResticException("snapshot_missing", "The backup point is no longer cached.");
            ShowChanges = true;
        });
    }

    /// <summary>
    /// Shows a changed entry in the content view, selected and ready to restore: a removed one in
    /// the backup point it was compared with (the only one still holding it).
    /// </summary>
    public Task<bool> ShowChangeInContentAsync(BackupChange change) => RunAsync(async actor =>
    {
        if (change.Kind == BackupChangeKind.Removed && BackupPoint is { } point)
        {
            // Never fall back to the selected point: it does not hold the removed entry.
            BackupPoint = BackupPoints.FirstOrDefault(p => p.RepositoryId == point.RepositoryId && p.SnapshotId == Changes?.BaseSnapshotId)
                          ?? throw new ResticException("snapshot_missing", "The backup point compared with no longer exists.");
        }
        ShowChanges = false;
        await BrowseCoreAsync(actor, ShareRelativePath.GetParent(change.Path));
        var name = ShareRelativePath.GetFileName(change.Path);
        if (BrowseEntries.Any(e => e.Name == name))
            SelectedEntries.Add(name);
    });

    private async Task LoadChangesCoreAsync(UserContext actor, string? baseSnapshotId)
    {
        Changes = null;
        _requestedBase = baseSnapshotId;
        if (BackupPoint is not { } point)
            return;
        var key = (point.RepositoryId, point.SnapshotId, baseSnapshotId);
        if (_changesCache.TryGetValue(key, out var cached))
        {
            Changes = cached;
            return;
        }
        // Not disposed: it holds no timer, and Cancel may race with the end of the diff.
        var cancel = _changesCts = new CancellationTokenSource();
        IsLoadingChanges = true;
        Changed?.Invoke();
        try
        {
            Changes = await catalog.GetChangesAsync(actor, point.RepositoryId, point.SnapshotId, baseSnapshotId, cancel.Token);
            if (_changesCache.Count + 2 > MaximumCachedComparisons)
                _changesCache.Clear();
            _changesCache[key] = Changes;
            // The default base chosen explicitly later is the same comparison.
            if (Changes.BaseSnapshotId is { } resolved)
                _changesCache[(point.RepositoryId, point.SnapshotId, resolved)] = Changes;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // Cancelled on purpose: no error, the view offers a retry.
        }
        finally
        {
            IsLoadingChanges = false;
            _changesCts = null;
        }
    }
}
