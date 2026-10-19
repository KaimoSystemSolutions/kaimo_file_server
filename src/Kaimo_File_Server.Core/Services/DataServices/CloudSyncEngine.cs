using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Storage;
using Kaimo_File_Server.Infrastructure.Clouds;

namespace Kaimo_File_Server.Core.Services.DataServices;

/// <summary>
/// One synchronization run of a local/remote directory pair (see
/// <see cref="ICloudConnection.SyncAsync"/>). Holds the per-run state (endpoints, mode,
/// options, manifests, failure sink, progress, cancellation) so the recursive
/// reconciliation only passes the paths it works on. Single use: create one per run.
/// </summary>
internal sealed partial class CloudSyncEngine
{
    private readonly ICloudConnection _remote;
    private readonly IFileService _local;
    private readonly UserContext _user;
    private readonly SyncMode _mode;
    private readonly CloudSyncTransferOptions _options;

    // Paths present after the previous run (null unless delete propagation is active). An
    // item on only one side that appears here was deleted on the other endpoint; one that
    // does not is newly created.
    private readonly SyncManifest? _previousManifest;

    // Accumulator for every path that remains present after this run.
    private readonly SyncManifest _newManifest = new();

    private readonly ICollection<SyncFailure>? _failures;
    private readonly SyncProgress _progress;
    private readonly CancellationToken _cancellationToken;

    // The size pre-scan already walks the remote tree; capture every listing
    // it produces so reconciliation reuses them instead of re-listing the
    // whole remote tree a second time (one network round-trip per directory).
    // ponytail: holds the full remote tree's metadata in memory for the run;
    // fuse listing and reconciliation into one streaming walk if that ever
    // matters on very large trees.
    private readonly Dictionary<string, IReadOnlyList<CloudItemMeta>> _remoteListings =
        new(StringComparer.OrdinalIgnoreCase);

    public CloudSyncEngine(
        ICloudConnection remote,
        IFileService local,
        UserContext user,
        SyncMode mode,
        CloudSyncTransferOptions? transferOptions,
        SyncManifest? previousManifest,
        Action<string?, int>? reportProgress,
        ICollection<SyncFailure>? failures,
        CancellationToken cancellationToken)
    {
        _remote = remote;
        _local = local;
        _user = user;
        _mode = mode;
        _options = transferOptions ?? new CloudSyncTransferOptions(null);
        _failures = failures;
        _progress = new SyncProgress { Report = reportProgress };
        _cancellationToken = cancellationToken;

        // A manifest of the converged state is maintained for every two-way run,
        // even when delete propagation is off, so that enabling the option later
        // has a baseline immediately instead of wasting the first run establishing
        // one. The previous manifest is only *consulted* (to delete) when the
        // option is on; otherwise deletions are ignored and items are copied.
        // Pull and push also build one — not for delete propagation, but so the file
        // browser can tell a remote-backed item (present here) from a local-only one
        // (absent) and flag the latter as "won't be uploaded" / "pending upload"
        // instead of trusting its (possibly preserved, older) write time.
        bool applyDeletions = _options.SyncDeletions && mode == SyncMode.TwoWay;
        _previousManifest = applyDeletions ? previousManifest : null;
    }

    /// <summary>Runs the synchronization and returns the manifest of the converged state.</summary>
    public async Task<SyncManifest?> RunAsync(string remotePath, string localPath)
    {
        _cancellationToken.ThrowIfCancellationRequested();

        _progress.TotalBytes = _mode switch
        {
            SyncMode.Push => await Guard(SyncEndpoint.Local,
                () => _local.GetDirectorySizeAsync(localPath, _user)),
            SyncMode.Pull => await Guard(SyncEndpoint.Remote,
                () => ListRemoteTreeAsync(remotePath)),
            SyncMode.TwoWay => Math.Max(
                await Guard(SyncEndpoint.Local,
                    () => _local.GetDirectorySizeAsync(localPath, _user)),
                await Guard(SyncEndpoint.Remote,
                    () => ListRemoteTreeAsync(remotePath))),
            _ => 0
        };

        _cancellationToken.ThrowIfCancellationRequested();

        _progress.Update("Scanning...");

        await SyncDirectoryAsync(remotePath.TrimEnd('/'), localPath.TrimEnd('/'), isRoot: true);

        return _newManifest;
    }

    /// <summary>
    /// Recursively lists the remote tree once, caching every directory's listing
    /// by path and returning the total byte size. Replaces a separate size-only
    /// walk so reconciliation can reuse these listings instead of re-listing the
    /// whole remote tree, halving remote metadata round-trips on pull/two-way.
    /// </summary>
    private async Task<long> ListRemoteTreeAsync(string remoteDir)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        // Match the exact path SyncDirectoryAsync lists and looks up by, so the cache
        // hits: the trimmed directory at the root and "{dir}/{name}" for children,
        // never the provider's own item.Path (which may be normalized differently).
        string dir = remoteDir.TrimEnd('/');
        var items = await _remote.ListAsync(dir, _cancellationToken);
        _remoteListings[dir] = items;

        long total = 0;
        foreach (var item in items)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            total += item.IsDirectory
                ? await ListRemoteTreeAsync($"{dir}/{item.Name}")
                : item.Size;
        }
        return total;
    }

    /// <summary>
    /// Tags a "directory no longer exists" failure with the endpoint it came from
    /// so the health surface can report a specific cause (local vs remote folder
    /// removed) instead of a generic sync failure. Other exceptions propagate
    /// unchanged.
    /// </summary>
    private static async Task<T> Guard<T>(SyncEndpoint endpoint, Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (DirectoryNotFoundException exception)
        {
            throw new SyncDirectoryMissingException(endpoint, exception);
        }
    }

    /// <summary>
    /// Compares timestamps with a tolerance because providers store modification
    /// times at coarser precision than the local file system (Dropbox, SFTP v3 and
    /// WebDAV <c>getlastmodified</c> keep whole seconds, FAT two). A tighter window
    /// makes a just-pushed file look changed again on the next run. Inside the window
    /// a size mismatch still proves a real change, so the raw order picks the
    /// direction (identical times give no direction, so nothing transfers).
    /// </summary>
    private static int CompareModifiedTime(
        DateTime local,
        long localSize,
        DateTime remote,
        long remoteSize)
    {
        var difference = local - remote;

        if (Math.Abs(difference.TotalMilliseconds) < 2000
            && (localSize == remoteSize || difference == TimeSpan.Zero))
            return 0;

        return difference > TimeSpan.Zero ? 1 : -1;
    }

    /// <summary>
    /// Keys a remote listing by the local name of each item. Names Windows cannot use are
    /// stored locally renamed ("a:b" → "a_b"), so keying by that name pairs the two sides on
    /// every run instead of re-pulling, pushing back a twin or deleting the local copy. A local
    /// entry that already has the original name (created before this rule) keeps it. When a
    /// renamed item collides with another remote item, the exact name wins and the renamed one
    /// is reported and skipped. Duplicate exact names still throw, as before.
    /// </summary>
    private Dictionary<string, CloudItemMeta> KeyByLocalName(
        IEnumerable<CloudItemMeta> remoteListing,
        IReadOnlyDictionary<string, FileMetadata> localItems,
        string remoteDir)
    {
        var items = new Dictionary<string, CloudItemMeta>(StringComparer.OrdinalIgnoreCase);
        var renamed = new List<CloudItemMeta>();
        foreach (var item in remoteListing)
        {
            if (WindowsFileNameHelper.IsValid(item.Name) || localItems.ContainsKey(item.Name))
                items.Add(item.Name, item);
            else
                renamed.Add(item);
        }

        foreach (var item in renamed)
        {
            var localName = WindowsFileNameHelper.Sanitize(item.Name);
            if (!items.TryAdd(localName, item))
                _failures?.Add(new SyncFailure($"{remoteDir}/{item.Name}", SyncFailureOperation.Download,
                    $"'{item.Name}' is not valid on Windows and its replacement name '{localName}' is already used by another remote item."));
        }

        return items;
    }

    /// <summary>
    /// Recursively reconciles one directory level according to the selected sync
    /// mode and checks cancellation before each child is processed.
    /// </summary>
    /// <returns>
    /// On which side this pass changed the directory's direct children. The caller uses it to
    /// give the directory the other side's time once its content is done (see
    /// <see cref="StampDirectoryAsync"/>).
    /// </returns>
    private async Task<(bool Local, bool Remote)> SyncDirectoryAsync(
        string remoteDir, string localDir, bool isRoot)
    {
        _cancellationToken.ThrowIfCancellationRequested();

        // Listing failures on the configured root are hard errors (a removed or
        // inaccessible sync folder is a configuration problem, surfaced via
        // SyncDirectoryMissingException). Below the root they are recorded and the
        // subtree is skipped, so one unreadable folder cannot abort the whole run.
        Dictionary<string, CloudItemMeta> remoteItems;
        Dictionary<string, FileMetadata> localItems;
        try
        {
            // The size pre-scan already listed the remote tree; reuse its snapshot
            // so this directory is not fetched from the provider a second time.
            // A cache miss (e.g. a subtree created remotely mid-run) falls back to
            // a live listing so nothing is skipped.
            var remoteListing = _remoteListings.TryGetValue(remoteDir, out var cachedRemote)
                ? cachedRemote
                : await Guard(SyncEndpoint.Remote,
                    () => _remote.ListAsync(remoteDir, _cancellationToken));

            localItems = (await Guard(SyncEndpoint.Local,
                    () => _local.ListAsync(localDir, _user)))
                .ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);

            remoteItems = KeyByLocalName(remoteListing, localItems, remoteDir);
        }
        catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SyncDirectoryMissingException) when (!isRoot)
        {
            // The subfolder was removed after its parent was listed (e.g. deleted
            // while this run walks the tree). Not a failure: the parent already
            // recorded it in the manifest, so the next run reconciles it like any
            // other deletion instead of reporting one error per vanished child.
            return (false, false);
        }
        catch (Exception exception) when (!isRoot)
        {
            _failures?.Add(new SyncFailure(localDir, SyncFailureOperation.List, exception.Message));
            return (false, false);
        }
        _cancellationToken.ThrowIfCancellationRequested();

        // Which side this pass wrote into; adding or removing an entry moves a folder's mtime.
        bool localChanged = false;
        bool remoteChanged = false;

        var allNames = new HashSet<string>(
            remoteItems.Keys.Concat(localItems.Keys),
            StringComparer.OrdinalIgnoreCase);

        foreach (var name in allNames)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            remoteItems.TryGetValue(name, out var remote);
            localItems.TryGetValue(name, out var local);

            if ((local is { IsDirectory: false } && _options.ShouldSkip(local.Name, local.Size))
                || (remote is { IsDirectory: false } && _options.ShouldSkip(remote.Name, remote.Size)))
                continue;

            // name is the local name; a renamed remote item keeps its original name remotely.
            string remoteChild = $"{remoteDir}/{remote?.Name ?? name}";
            string localChild = $"{localDir}/{name}";

            // The recycle bin and internal Kaimo namespaces are share bookkeeping,
            // not user content. Skipping them keeps a locally recycled file from
            // being re-uploaded (which would otherwise defeat delete propagation).
            if (ShareEntryPolicy.Classify(localChild).Kind != ShareEntryKind.Regular)
                continue;

            (bool Local, bool Remote) changed;
            if (local != null && remote == null)
                changed = await ReconcileLocalOnlyAsync(local, localChild, remoteChild);
            else if (local == null && remote != null)
                changed = await ReconcileRemoteOnlyAsync(remote, localChild, remoteChild);
            else if (local!.IsDirectory && remote!.IsDirectory)
                changed = await ReconcileBothDirectoriesAsync(local, remote, localChild, remoteChild);
            else if (local.IsDirectory != remote!.IsDirectory)
                continue;
            else
                changed = await ReconcileBothFilesAsync(local, remote, localChild, remoteChild);

            localChanged |= changed.Local;
            remoteChanged |= changed.Remote;
        }

        return (localChanged, remoteChanged);
    }

    /// <summary>An item that exists only locally.</summary>
    private async Task<(bool Local, bool Remote)> ReconcileLocalOnlyAsync(
        FileMetadata local, string localChild, string remoteChild)
    {
        if (_mode == SyncMode.Pull)
        {
            // Pull mirror: with delete propagation on, the remote is
            // authoritative, so an item present only locally (including one
            // created locally that was never uploaded) is removed to keep the
            // local tree a 1:1 copy of the remote. The delete honors the
            // share recycle bin. Off: local-only items are left untouched.
            if (!_options.SyncDeletions)
                return (false, false);

            _progress.Update($"Removing {local.Name}...");
            return (await DeleteLocalAsync(localChild), false);
        }

        // Present locally, absent remotely, and known from the last run:
        // it was deleted remotely, so remove it locally instead of
        // re-uploading it. The local delete honors the share recycle bin.
        if (_previousManifest?.Contains(localChild) == true)
        {
            _progress.Update($"Removing {local.Name}...");
            return (await DeleteLocalAsync(localChild), false);
        }

        if (local.IsDirectory)
        {
            // Only record the new subtree in the manifest once its remote
            // directory exists; otherwise a failed create would let a later
            // run treat the local-only folder as remotely deleted.
            if (!await TryTransferAsync(remoteChild, SyncFailureOperation.CreateDirectory,
                    () => _remote.CreateDirectoryAsync(remoteChild, _cancellationToken)))
                return (false, false);

            _newManifest.Paths.Add(localChild);
            await UploadDirectoryRecursiveAsync(localChild, remoteChild, local.ModifiedAt);
            return (false, true);
        }

        if (await PushFileAsync(localChild, remoteChild, local.Name, local.ModifiedAt, local.Size))
        {
            // Manifest only on success: a file that failed to upload does
            // not exist remotely, so it must not look converged next run.
            _newManifest.Paths.Add(localChild);
            return (false, true);
        }

        return (false, false);
    }

    /// <summary>An item that exists only remotely.</summary>
    private async Task<(bool Local, bool Remote)> ReconcileRemoteOnlyAsync(
        CloudItemMeta remote, string localChild, string remoteChild)
    {
        if (_mode == SyncMode.Push)
            return (false, false);

        // Present remotely, absent locally, and known from the last run:
        // it was deleted locally, so remove it remotely instead of
        // re-downloading it.
        if (_previousManifest?.Contains(localChild) == true)
        {
            _progress.Update($"Removing {remote.Name}...");
            bool deleted = await TryTransferAsync(remoteChild, SyncFailureOperation.Delete,
                () => _remote.DeleteAsync(remoteChild, remote.IsDirectory, _cancellationToken));
            return (false, deleted);
        }

        if (remote.IsDirectory)
        {
            // Creating the folder would also recreate a parent deleted
            // locally mid-run (see LocalParentExistsAsync); skip it then.
            bool parentGone = false;
            if (await TryTransferAsync(localChild, SyncFailureOperation.CreateDirectory,
                    async () =>
                    {
                        if (!await LocalParentExistsAsync(localChild))
                        {
                            parentGone = true;
                            return;
                        }
                        await _local.CreateDirectoryAsync(localChild, _user);
                    }) && !parentGone)
            {
                _newManifest.Paths.Add(localChild);
                var created = await SyncDirectoryAsync(remoteChild, localChild, isRoot: false);
                // Created by this pass, so the new folder counts as changed locally.
                await StampDirectoryAsync(localChild, remoteChild,
                    (true, created.Remote), local: null, remote);
                return (true, false);
            }
            return (false, false);
        }

        if (await PullFileAsync(localChild, remoteChild, remote.Name, remote.ModifiedAt, remote.Size))
        {
            _newManifest.Paths.Add(localChild);
            return (true, false);
        }

        return (false, false);
    }

    /// <summary>A directory on both sides: recurse, then fix the folder time.</summary>
    private async Task<(bool Local, bool Remote)> ReconcileBothDirectoriesAsync(
        FileMetadata local, CloudItemMeta remote, string localChild, string remoteChild)
    {
        _newManifest.Paths.Add(localChild);
        var changes = await SyncDirectoryAsync(remoteChild, localChild, isRoot: false);
        await StampDirectoryAsync(localChild, remoteChild, changes, local, remote);
        return (false, false);
    }

    /// <summary>
    /// A file on both sides: the newer copy wins in the directions the mode allows
    /// (pull: remote → local, push: local → remote, two-way: both).
    /// </summary>
    private async Task<(bool Local, bool Remote)> ReconcileBothFilesAsync(
        FileMetadata local, CloudItemMeta remote, string localChild, string remoteChild)
    {
        // Present on both sides as files: it stays in the converged manifest
        // regardless of whether the newer-copy reconciliation succeeds, because
        // the file still exists on both endpoints either way.
        _newManifest.Paths.Add(localChild);

        int newer = CompareModifiedTime(local.ModifiedAt, local.Size, remote.ModifiedAt, remote.Size);

        if (newer > 0 && _mode != SyncMode.Pull)
            return (false, await PushFileAsync(localChild, remoteChild,
                local.Name, local.ModifiedAt, local.Size));

        if (newer < 0 && _mode != SyncMode.Push)
            return (await PullFileAsync(localChild, remoteChild,
                remote.Name, remote.ModifiedAt, remote.Size), false);

        return (false, false);
    }

    private Task<bool> DeleteLocalAsync(string localChild)
        => TryTransferAsync(localChild, SyncFailureOperation.Delete,
            () => _local.DeleteFileAsync(localChild, _user, _options.HonorRecycleBin));

    /// <summary>
    /// Gives a directory the source side's time once its content is synced (post-order, as
    /// <c>rsync -t</c> does): writing children moved the target folder's mtime to "now". Only a
    /// one-sided change has a source; a folder changed on both sides keeps its times. Providers
    /// without folder times (Dropbox) report <see cref="DateTime.MinValue"/> and are skipped.
    /// Best-effort: a failed stamp never fails the run.
    /// </summary>
    private async Task StampDirectoryAsync(
        string localDir,
        string remoteDir,
        (bool Local, bool Remote) changes,
        FileMetadata? local,
        CloudItemMeta remote)
    {
        try
        {
            if (changes.Local && !changes.Remote && remote.ModifiedAt > DateTime.MinValue)
                await _local.SetModifiedAtAsync(localDir, _user, remote.ModifiedAt);
            else if (changes.Remote && !changes.Local && local is not null && local.ModifiedAt > DateTime.MinValue)
                await _remote.TrySetDirectoryModifiedTimeAsync(remoteDir, local.ModifiedAt, _cancellationToken);
        }
        catch (Exception) when (!_cancellationToken.IsCancellationRequested)
        {
            // Best-effort: the folder then just keeps the sync time.
        }
    }
}
