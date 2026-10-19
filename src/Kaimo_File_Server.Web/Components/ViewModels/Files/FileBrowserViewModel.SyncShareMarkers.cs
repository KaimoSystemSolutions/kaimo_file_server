using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Services.DataServices;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public partial class FileBrowserViewModel
{
    // Normalized root paths of the loaded share's public links (with the owning link's id, so
    // an emblem can jump to it), so the browser can mark a shared folder (and everything beneath
    // it). Loaded per share; empty for the public browser.
    private List<(string Root, Guid LinkId, ShareLinkKind Kind)> _sharedRoots = [];

    // Sync local destinations for the loaded share, ordered most-specific (longest
    // LocalPath) first so a nested sync wins over an ancestor one. Loaded per share,
    // not per directory, and reused across in-place directory refreshes.
    private List<(string LocalPath, SyncMode Mode, string Name, DateTime? LastSuccessfulRunAtUtc, HashSet<string>? RemotePaths, Guid Id)> _shareSyncs = [];

    // Normalized local paths of the loaded share's syncs that currently have a job queued
    // or running. Replaced (never mutated) so render-time reads need no lock.
    private HashSet<string> _activeSyncPaths = [];

    // Bumped by every sync-state load (and by a share switch), so a slower, older load that
    // finishes last can never overwrite newer state — neither another share's syncs nor a
    // last-success time read before the run that just ended.
    private int _syncLoadGeneration;

    // Loads the enabled sync local destinations for the loaded share. Sync config is
    // small (a handful of rows), so an in-memory filter over the admin projection is
    // cheaper than a dedicated query and keeps the repository surface unchanged.
    private async Task LoadShareSyncsAsync(Guid shareId)
    {
        var generation = Interlocked.Increment(ref _syncLoadGeneration);
        var all = await _syncRepo.GetAllAsync();
        if (generation != Volatile.Read(ref _syncLoadGeneration))
            return; // superseded by a newer load or a share switch
        _shareSyncs = all
            .Where(entry => entry.Definition.Enabled && entry.Definition.LocalShareId == shareId)
            .Select(entry => (
                LocalPath: ShareRelativePath.Normalize(entry.Definition.LocalPath),
                entry.Definition.Mode,
                Name: string.IsNullOrWhiteSpace(entry.Definition.DisplayName)
                    ? (entry.Definition.LocalPath.Length == 0 ? CurrentShare?.Name ?? "" : entry.Definition.LocalPath)
                    : entry.Definition.DisplayName,
                entry.Runtime?.LastSuccessfulRunAtUtc,
                // The converged manifest says which paths the remote actually backs.
                RemotePaths: RemoteBackedPaths(entry.Runtime?.LastSyncManifest),
                entry.Definition.Id))
            .OrderByDescending(sync => sync.LocalPath.Length)
            .ToList();
        _activeSyncPaths = ActiveSyncPaths(shareId);
    }

    // Local paths of this share's syncs that have a job queued or running in the runner.
    private HashSet<string> ActiveSyncPaths(Guid shareId)
        => _syncJobs?.Jobs
               .Where(job => job.ShareId == shareId)
               .Select(job => ShareRelativePath.Normalize(job.LocalPath))
               .ToHashSet(StringComparer.Ordinal)
           ?? [];

    // Raised by the singleton runner on a background thread, also for every progress tick.
    // Only a change in which syncs are active matters: a job that started flips its items to
    // "syncing", and one that ended leaves a new last-success time and manifest behind, so
    // the sync state is reloaded and the emblems re-evaluated without navigating.
    private void OnSyncJobsChanged()
    {
        if (CurrentShare is not { } share || !Capabilities.HasCloudSync)
            return;
        var active = ActiveSyncPaths(share.Id);
        if (active.SetEquals(_activeSyncPaths))
            return;
        // Record the new set right away: a run that ends while the reload for its start is
        // still pending must compare against this set, not the stale one, or its end is missed.
        _activeSyncPaths = active;
        _ = ReloadSyncStateAsync(share.Id);
    }

    private async Task ReloadSyncStateAsync(Guid shareId)
    {
        try
        {
            await LoadShareSyncsAsync(shareId);
            OnStateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to reload sync state for share {ShareId}", shareId);
        }
    }

    // Normalizes a persisted sync manifest into a share-relative, case-insensitive set.
    // Returns null when no manifest has been recorded yet (a pull that has not run under
    // manifest tracking) so the caller falls back to the timestamp heuristic instead of
    // flagging every item as local-only. The manifest is written with the engine's raw
    // "{dir}/{name}" paths (which can carry a leading slash at a root sync), so each entry
    // is renormalized to the canonical form the browser compares against.
    private static HashSet<string>? RemoteBackedPaths(string? manifestJson)
    {
        var manifest = SyncManifest.Deserialize(manifestJson);
        if (manifest is null)
            return null;

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in manifest.Paths)
            paths.Add(ShareRelativePath.Normalize(path));
        return paths;
    }

    /// <summary>
    /// Whether the entry carries a public share link, or lives beneath a shared folder — so the
    /// browser can flag it (and everything under a shared folder) as shared.
    /// </summary>
    public bool IsShared(FileMetadata entry)
    {
        if (_sharedRoots.Count == 0) return false;

        var rel = ShareRelativePath.Normalize(ShareRelativeOf(entry));
        foreach (var (root, _, _) in _sharedRoots)
        {
            if (string.Equals(rel, root, StringComparison.OrdinalIgnoreCase)) return true;
            if (root.Length == 0 || rel.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <inheritdoc />
    public Guid? GetShareLinkId(FileMetadata entry) => FindShareLink(entry)?.LinkId;

    /// <inheritdoc />
    public ShareLinkKind? GetShareLinkKind(FileMetadata entry) => FindShareLink(entry)?.Kind;

    /// <inheritdoc />
    public Guid? GetShareLinkId(FileMetadata entry, ShareLinkKind kind) => FindShareLink(entry, kind)?.LinkId;

    // The nearest (longest-root) link covering the entry, optionally of one kind only, or null.
    private (Guid LinkId, ShareLinkKind Kind)? FindShareLink(FileMetadata entry, ShareLinkKind? onlyKind = null)
    {
        if (_sharedRoots.Count == 0) return null;

        var rel = ShareRelativePath.Normalize(ShareRelativeOf(entry));
        (Guid LinkId, ShareLinkKind Kind)? best = null;
        var bestLength = -1;
        // Nearest (longest) matching root wins, so an entry with its own link jumps to that
        // link rather than an ancestor's.
        foreach (var (root, linkId, kind) in _sharedRoots)
        {
            if (onlyKind is not null && kind != onlyKind) continue;
            var matches = string.Equals(rel, root, StringComparison.OrdinalIgnoreCase)
                          || root.Length == 0
                          || rel.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
            if (matches && root.Length > bestLength)
            {
                best = (linkId, kind);
                bestLength = root.Length;
            }
        }
        return best;
    }

    /// <summary>
    /// Returns the sync marker for an entry that is, or lives beneath, a sync's local
    /// destination folder. The most specific (nearest) sync wins when several apply.
    /// </summary>
    public SyncFolderMarker? GetSyncMarker(FileMetadata entry)
    {
        if (_shareSyncs.Count == 0)
            return null;

        var rel = ShareRelativePath.Normalize(ShareRelativeOf(entry));
        foreach (var sync in _shareSyncs)
        {
            // The sync's own destination folder always shows the plain sync emblem: it
            // anchors the sync and must never inherit a warning from a stray child (it is
            // never recorded in its own manifest, and its write time moves with any child).
            if (sync.LocalPath.Length > 0 && rel == sync.LocalPath)
                return new SyncFolderMarker(sync.Mode, sync.Name, SyncItemState.Synced, sync.Id);

            if (sync.LocalPath.Length == 0
                || rel.StartsWith(sync.LocalPath + "/", StringComparison.Ordinal))
            {
                // null (no manifest yet) tells the evaluator to fall back to the
                // timestamp heuristic; a set means membership is authoritative.
                bool? isRemoteBacked = sync.RemotePaths?.Contains(rel);
                var state = SyncItemStateEvaluator.Evaluate(
                    entry.ModifiedAt, sync.LastSuccessfulRunAtUtc, sync.Mode, isRemoteBacked,
                    isRunning: _activeSyncPaths.Contains(sync.LocalPath));
                return new SyncFolderMarker(sync.Mode, sync.Name, state, sync.Id);
            }
        }

        return null;
    }
}
