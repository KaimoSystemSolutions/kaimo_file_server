using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Storage;
using Kaimo_File_Server.Infrastructure.Clouds;

namespace Kaimo_File_Server.Core.Services.DataServices;

/// <summary>
/// Provider-neutral cloud storage contract used by the synchronization engine.
/// Implementations must honor cancellation tokens for network-bound operations
/// so a UI cancellation stops real work rather than only hiding its progress.
/// </summary>
public interface ICloudConnection
{
    Task Dispose();
    string ServiceName { get; }

    /// <summary>
    /// Indicates that the provider rotated persisted credentials while the
    /// connection was in use. Callers should save the owning SyncedFolder and
    /// acknowledge the change only after persistence succeeded.
    /// </summary>
    bool HasPendingCredentialChanges => false;

    /// <summary>
    /// Returns only the credential fields that changed at runtime. This allows
    /// callers to merge them into a freshly loaded aggregate without overwriting
    /// concurrent edits to paths or sync settings.
    /// </summary>
    IReadOnlyDictionary<string, string> GetPendingCredentialChanges()
        => new Dictionary<string, string>();

    /// <summary>Marks provider credential changes as successfully persisted.</summary>
    void AcknowledgeCredentialChanges()
    {
    }

    /// <summary>
    /// Returns optional account metadata without exposing a provider-specific SDK type.
    /// The default keeps providers that do not offer account profiles lightweight.
    /// </summary>
    Task<CloudAccountInfo?> GetAccountInfoAsync() => Task.FromResult<CloudAccountInfo?>(null);

    /// <summary>Creates or replaces a remote file while preserving its source timestamp.</summary>
    Task UploadAsync(
        string path,
        Stream data,
        DateTime modifiedTime,
        CancellationToken cancellationToken = default);
    /// <summary>Streams a remote file into the supplied target stream.</summary>
    Task DownloadAsync(
        string path,
        Stream target,
        CancellationToken cancellationToken = default);
    /// <summary>Creates a remote directory, including missing parents where supported.</summary>
    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a remote file or directory (recursively for directories). Used by
    /// two-way delete propagation. The default throws so a provider that cannot
    /// delete fails loudly instead of silently resurrecting the item.
    /// </summary>
    Task DeleteAsync(string path, bool isDirectory, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            $"{ServiceName} does not support remote deletion required for delete synchronization.");

    /// <summary>Recursively calculates the total size used for sync progress reporting.</summary>
    Task<long> GetDirectorySizeAsync(string path, CancellationToken cancellationToken = default);
    
    /// <summary>Lists the direct children of a remote directory.</summary>
    Task<IReadOnlyList<CloudItemMeta>> ListAsync(
        string path,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronizes one configured local/remote directory pair. Cancellation is
    /// checked between local file-service calls and is forwarded to every cloud
    /// operation so long-running uploads and downloads can stop immediately.
    /// </summary>
    /// <param name="previousManifest">
    /// The set of share-relative paths that existed after the previous successful
    /// run, used to distinguish a new item from one deleted on the other endpoint.
    /// Pass <c>null</c> when delete propagation is off or no baseline exists yet.
    /// </param>
    /// <returns>
    /// The manifest of paths present after this run when delete propagation is
    /// active (persist it for the next run), otherwise <c>null</c>.
    /// </returns>
    /// <param name="failures">
    /// Optional sink for per-item failures. When supplied, a single file that
    /// cannot be transferred is recorded here and the run continues with the
    /// next item instead of aborting; the caller reports the collected list
    /// after the run. A missing configured root directory still fails the whole
    /// run (it is a configuration error, not a per-item problem).
    /// </param>
    async Task<SyncManifest?> SyncAsync(
        IFileService fileService,
        UserContext user,
        string remotePath,
        string localPath,
        SyncMode mode,
        CloudSyncTransferOptions? transferOptions = null,
        SyncManifest? previousManifest = null,
        Action<string?, int>? reportProgress = null,
        ICollection<SyncFailure>? failures = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // The size pre-scan already walks the remote tree; capture every listing
        // it produces so reconciliation reuses them instead of re-listing the
        // whole remote tree a second time (one network round-trip per directory).
        // ponytail: holds the full remote tree's metadata in memory for the run;
        // fuse listing and reconciliation into one streaming walk if that ever
        // matters on very large trees.
        var remoteListings = new Dictionary<string, IReadOnlyList<CloudItemMeta>>(
            StringComparer.OrdinalIgnoreCase);
        long totalBytes = mode switch
        {
            SyncMode.Push => await Guard(SyncEndpoint.Local,
                () => fileService.GetDirectorySizeAsync(localPath, user)),
            SyncMode.Pull => await Guard(SyncEndpoint.Remote,
                () => ListRemoteTreeAsync(remotePath, remoteListings, cancellationToken)),
            SyncMode.TwoWay => Math.Max(
                await Guard(SyncEndpoint.Local,
                    () => fileService.GetDirectorySizeAsync(localPath, user)),
                await Guard(SyncEndpoint.Remote,
                    () => ListRemoteTreeAsync(remotePath, remoteListings, cancellationToken))),
            _ => 0
        };

        cancellationToken.ThrowIfCancellationRequested();

        var progress = new SyncProgress
        {
            TotalBytes = totalBytes,
            Report = reportProgress
        };

        progress.Update("Scanning...");

        var options = transferOptions ?? new CloudSyncTransferOptions(null);

        // A manifest of the converged state is maintained for every two-way run,
        // even when delete propagation is off, so that enabling the option later
        // has a baseline immediately instead of wasting the first run establishing
        // one. The previous manifest is only *consulted* (to delete) when the
        // option is on; otherwise deletions are ignored and items are copied.
        // Pull also builds a manifest — not for delete propagation, but so the file
        // browser can tell a remote-backed item (present here) from a local-only one
        // (absent) and flag the latter as "won't be uploaded".
        bool buildManifest = mode == SyncMode.TwoWay || mode == SyncMode.Pull;
        bool applyDeletions = options.SyncDeletions && mode == SyncMode.TwoWay;
        var newManifest = buildManifest ? new SyncManifest() : null;

        await SyncDirectory(
            fileService,
            user,
            remotePath.TrimEnd('/'),
            localPath.TrimEnd('/'),
            mode,
            options,
            progress,
            applyDeletions ? previousManifest : null,
            newManifest,
            failures,
            remoteListings,
            isRoot: true,
            cancellationToken
            );

        return newManifest;
    }

    /// <summary>
    /// Recursively lists the remote tree once, caching every directory's listing
    /// by path and returning the total byte size. Replaces a separate size-only
    /// walk so reconciliation can reuse these listings instead of re-listing the
    /// whole remote tree, halving remote metadata round-trips on pull/two-way.
    /// </summary>
    private async Task<long> ListRemoteTreeAsync(
        string remoteDir,
        IDictionary<string, IReadOnlyList<CloudItemMeta>> cache,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Match the exact path SyncDirectory lists and looks up by, so the cache
        // hits: the trimmed directory at the root and "{dir}/{name}" for children,
        // never the provider's own item.Path (which may be normalized differently).
        string dir = remoteDir.TrimEnd('/');
        var items = await ListAsync(dir, cancellationToken);
        cache[dir] = items;

        long total = 0;
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            total += item.IsDirectory
                ? await ListRemoteTreeAsync($"{dir}/{item.Name}", cache, cancellationToken)
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
    /// Compares timestamps with a small tolerance because cloud providers often
    /// round modification times differently from the local file system.
    /// </summary>
    private static int CompareModifiedTime(
        DateTime local,
        DateTime remote)
    {
        var difference = local - remote;

        if (Math.Abs(difference.TotalMilliseconds) < 5)
            return 0;

        return difference > TimeSpan.Zero ? 1 : -1;
    }
    /// <summary>
    /// Recursively reconciles one directory level according to the selected sync
    /// mode and checks cancellation before each child is processed.
    /// </summary>
    /// <param name="previousManifest">
    /// Paths present after the previous run (null unless delete propagation is
    /// active). An item on only one side that appears here was deleted on the
    /// other endpoint; one that does not is newly created.
    /// </param>
    /// <param name="newManifest">
    /// Accumulator for every path that remains present after this run; null unless
    /// delete propagation is active.
    /// </param>
    private async Task SyncDirectory(
        IFileService fileService,
        UserContext user,
        string remoteDir,
        string localDir,
        SyncMode mode,
        CloudSyncTransferOptions options,
        SyncProgress syncProgress,
        SyncManifest? previousManifest,
        SyncManifest? newManifest,
        ICollection<SyncFailure>? failures,
        IReadOnlyDictionary<string, IReadOnlyList<CloudItemMeta>> remoteListings,
        bool isRoot,
        CancellationToken cancellationToken
        )
    {
        cancellationToken.ThrowIfCancellationRequested();

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
            remoteItems = (remoteListings.TryGetValue(remoteDir, out var cachedRemote)
                    ? cachedRemote
                    : await Guard(SyncEndpoint.Remote,
                        () => ListAsync(remoteDir, cancellationToken)))
                .ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);

            localItems = (await Guard(SyncEndpoint.Local,
                    () => fileService.ListAsync(localDir, user)))
                .ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (!isRoot)
        {
            failures?.Add(new SyncFailure(localDir, SyncFailureOperation.List, exception.Message));
            return;
        }
        cancellationToken.ThrowIfCancellationRequested();

        var allNames = new HashSet<string>(
            remoteItems.Keys.Concat(localItems.Keys),
            StringComparer.OrdinalIgnoreCase);

        foreach (var name in allNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            remoteItems.TryGetValue(name, out var remote);
            localItems.TryGetValue(name, out var local);

            bool remoteExists = remote != null;
            bool localExists = local != null;

            if ((localExists && !local!.IsDirectory && options.ShouldSkip(local.Name, local.Size))
                || (remoteExists && !remote!.IsDirectory && options.ShouldSkip(remote.Name, remote.Size)))
                continue;

            string remoteChild = $"{remoteDir}/{name}";
            string localChild = $"{localDir}/{name}";

            // The recycle bin and internal Kaimo namespaces are share bookkeeping,
            // not user content. Skipping them keeps a locally recycled file from
            // being re-uploaded (which would otherwise defeat delete propagation).
            if (ShareEntryPolicy.Classify(localChild).Kind != ShareEntryKind.Regular)
                continue;

            //--------------------------------------------------
            // Only local
            //--------------------------------------------------
            if (localExists && !remoteExists)
            {
                if (mode == SyncMode.Pull)
                {
                    // Pull mirror: with delete propagation on, the remote is
                    // authoritative, so an item present only locally (including one
                    // created locally that was never uploaded) is removed to keep the
                    // local tree a 1:1 copy of the remote. The delete honors the
                    // share recycle bin. Off: local-only items are left untouched.
                    if (options.SyncDeletions)
                    {
                        syncProgress.Update($"Removing {local!.Name}...");
                        await TryTransferAsync(failures, localChild, SyncFailureOperation.Delete,
                            () => fileService.DeleteFileAsync(localChild, user, options.HonorRecycleBin),
                            cancellationToken);
                    }
                    continue;
                }

                // Present locally, absent remotely, and known from the last run:
                // it was deleted remotely, so remove it locally instead of
                // re-uploading it. The local delete honors the share recycle bin.
                if (previousManifest?.Contains(localChild) == true)
                {
                    syncProgress.Update($"Removing {local!.Name}...");
                    await TryTransferAsync(failures, localChild, SyncFailureOperation.Delete,
                        () => fileService.DeleteFileAsync(localChild, user, options.HonorRecycleBin),
                        cancellationToken);
                    continue;
                }

                if (local!.IsDirectory)
                {
                    // Only record the new subtree in the manifest once its remote
                    // directory exists; otherwise a failed create would let a later
                    // run treat the local-only folder as remotely deleted.
                    if (await TryTransferAsync(failures, remoteChild, SyncFailureOperation.CreateDirectory,
                            () => CreateDirectoryAsync(remoteChild, cancellationToken), cancellationToken))
                    {
                        newManifest?.Paths.Add(localChild);
                        await UploadDirectoryRecursive(
                            fileService,
                            user,
                            localChild,
                            remoteChild,
                            syncProgress,
                            options,
                            newManifest,
                            failures,
                            cancellationToken);
                    }
                }
                else if (await PushFileAsync(fileService, user, localChild, remoteChild,
                             local.Name, local.ModifiedAt, local.Size, options, syncProgress,
                             failures, cancellationToken))
                {
                    // Manifest only on success: a file that failed to upload does
                    // not exist remotely, so it must not look converged next run.
                    newManifest?.Paths.Add(localChild);
                }

                continue;
            }

            //--------------------------------------------------
            // Only remote
            //--------------------------------------------------
            if (!localExists && remoteExists)
            {
                if (mode == SyncMode.Push)
                    continue;

                // Present remotely, absent locally, and known from the last run:
                // it was deleted locally, so remove it remotely instead of
                // re-downloading it.
                if (previousManifest?.Contains(localChild) == true)
                {
                    syncProgress.Update($"Removing {remote!.Name}...");
                    await TryTransferAsync(failures, remoteChild, SyncFailureOperation.Delete,
                        () => DeleteAsync(remoteChild, remote.IsDirectory, cancellationToken),
                        cancellationToken);
                    continue;
                }

                if (remote!.IsDirectory)
                {
                    if (await TryTransferAsync(failures, localChild, SyncFailureOperation.CreateDirectory,
                            () => fileService.CreateDirectoryAsync(localChild, user), cancellationToken))
                    {
                        newManifest?.Paths.Add(localChild);
                        await SyncDirectory(
                            fileService,
                            user,
                            remoteChild,
                            localChild,
                            mode,
                            options,
                            syncProgress,
                            previousManifest,
                            newManifest,
                            failures,
                            remoteListings,
                            isRoot: false,
                            cancellationToken);
                    }
                }
                else if (await PullFileAsync(fileService, user, localChild, remoteChild,
                             remote.Name, remote.ModifiedAt, remote.Size, options, syncProgress,
                             failures, cancellationToken))
                {
                    newManifest?.Paths.Add(localChild);
                }

                continue;
            }

            //--------------------------------------------------
            // Both directories
            //--------------------------------------------------
            if (local!.IsDirectory && remote!.IsDirectory)
            {
                newManifest?.Paths.Add(localChild);
                await SyncDirectory(
                    fileService,
                    user,
                    remoteChild,
                    localChild,
                    mode,
                    options,
                    syncProgress,
                    previousManifest,
                    newManifest,
                    failures,
                    remoteListings,
                    isRoot: false,
                    cancellationToken);

                continue;
            }

            if (local!.IsDirectory != remote!.IsDirectory)
                continue;

            // Present on both sides as files: it stays in the converged manifest
            // regardless of whether the newer-copy reconciliation succeeds, because
            // the file still exists on both endpoints either way.
            newManifest?.Paths.Add(localChild);

            //--------------------------------------------------
            // Pull
            //--------------------------------------------------
            if (mode == SyncMode.Pull)
            {
                if (CompareModifiedTime(local.ModifiedAt, remote.ModifiedAt) < 0)
                    await PullFileAsync(fileService, user, localChild, remoteChild,
                        remote.Name, remote.ModifiedAt, remote.Size, options, syncProgress,
                        failures, cancellationToken);

                continue;
            }

            //--------------------------------------------------
            // Push
            //--------------------------------------------------
            if (mode == SyncMode.Push)
            {
                if (CompareModifiedTime(local.ModifiedAt, remote.ModifiedAt) > 0)
                    await PushFileAsync(fileService, user, localChild, remoteChild,
                        local.Name, local.ModifiedAt, local.Size, options, syncProgress,
                        failures, cancellationToken);

                continue;
            }

            //--------------------------------------------------
            // Two-way
            //--------------------------------------------------
            if (CompareModifiedTime(local.ModifiedAt, remote.ModifiedAt) > 0)
                await PushFileAsync(fileService, user, localChild, remoteChild,
                    local.Name, local.ModifiedAt, local.Size, options, syncProgress,
                    failures, cancellationToken);
            else if (CompareModifiedTime(local.ModifiedAt, remote.ModifiedAt) < 0)
                await PullFileAsync(fileService, user, localChild, remoteChild,
                    remote.Name, remote.ModifiedAt, remote.Size, options, syncProgress,
                    failures, cancellationToken);
        }
    }

    /// <summary>
    /// Uploads one local file to the remote endpoint with a transient-fault retry.
    /// Returns true only when the upload succeeded; a definitive failure is
    /// recorded in <paramref name="failures"/> and the caller continues.
    /// </summary>
    private async Task<bool> PushFileAsync(
        IFileService fileService,
        UserContext user,
        string localChild,
        string remoteChild,
        string displayName,
        DateTime modifiedAt,
        long size,
        CloudSyncTransferOptions options,
        SyncProgress syncProgress,
        ICollection<SyncFailure>? failures,
        CancellationToken cancellationToken)
    {
        syncProgress.Update($"Pushing {displayName}...");
        bool ok = await TryTransferAsync(failures, localChild, SyncFailureOperation.Upload, async () =>
        {
            await using var stream = options.LimitUpload(await fileService.ReadFileAsync(localChild, user));
            await UploadAsync(remoteChild, stream, modifiedAt, cancellationToken);
        }, cancellationToken);

        if (ok)
        {
            syncProgress.TransferredBytes += size;
            syncProgress.Update($"Pushing {displayName}...");
        }
        return ok;
    }

    /// <summary>
    /// Downloads one remote file to the local endpoint with a transient-fault
    /// retry. The payload streams straight through an <see cref="IFileSession"/>
    /// write handle, so it is written to local storage exactly once (no temp-file
    /// round-trip) while staying bounded in memory. Returns true only when the
    /// download succeeded.
    /// </summary>
    private async Task<bool> PullFileAsync(
        IFileService fileService,
        UserContext user,
        string localChild,
        string remoteChild,
        string displayName,
        DateTime modifiedAt,
        long size,
        CloudSyncTransferOptions options,
        SyncProgress syncProgress,
        ICollection<SyncFailure>? failures,
        CancellationToken cancellationToken)
    {
        syncProgress.Update($"Pulling {displayName}...");
        bool ok = await TryTransferAsync(failures, localChild, SyncFailureOperation.Download, async () =>
        {
            // Open a write handle and stream the download directly into it. The
            // session's dispose runs the same versioning/ownership/change-log and
            // search-index hooks as WriteFileAsync, so behavior is preserved.
            var open = await fileService.OpenAsync(
                localChild, OpenMode.CreateOrTruncate, AccessIntent.Write, ShareIntent.None,
                user, cancellationToken);
            await using var session = open.Session;
            await using (var sink = options.LimitDownload(new FileSessionWriteStream(session)))
            {
                await DownloadAsync(remoteChild, sink, cancellationToken);
                await sink.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Stamp the source modification time so pull change-detection keeps
            // working on the next run (mirrors the former SetModifiedAtAsync call).
            await session.SetTimesAsync(new FileTimes(null, modifiedAt, null), cancellationToken);
        }, cancellationToken);

        if (ok)
        {
            syncProgress.TransferredBytes += size;
            syncProgress.Update($"Pulling {displayName}...");
        }
        return ok;
    }

    /// <summary>
    /// Classifies an exception as a transient fault worth retrying. Definitive
    /// provider rejections (4xx other than 429) are not retried so a permanently
    /// forbidden or missing item fails fast instead of stalling the run.
    /// </summary>
    private static bool IsTransient(Exception exception) => exception switch
    {
        ProviderRequestException providerError =>
            providerError.StatusCode is null
            || (int)providerError.StatusCode.Value >= 500
            || (int)providerError.StatusCode.Value == 429,
        HttpRequestException => true,
        System.Net.Sockets.SocketException => true,
        TimeoutException => true,
        IOException => true,
        _ => false
    };

    /// <summary>
    /// Runs one item's transfer, retrying a few times on transient faults with a
    /// short back-off. A genuine cancellation is rethrown so it is never bucketed
    /// as a per-item failure; any other definitive error is recorded and the run
    /// continues with the next item. Returns true only on success.
    /// </summary>
    // ponytail: fixed 3-attempt ceiling, per file; enough for transient network blips.
    private static async Task<bool> TryTransferAsync(
        ICollection<SyncFailure>? failures,
        string path,
        SyncFailureOperation operation,
        Func<Task> action,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await action();
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (attempt < maxAttempts && IsTransient(exception))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt * attempt), cancellationToken);
            }
            catch (Exception exception)
            {
                failures?.Add(new SyncFailure(path, operation, exception.Message));
                return false;
            }
        }
    }

    /// <summary>
    /// Uploads a local-only directory tree while forwarding progress and the
    /// cancellation token into each provider call.
    /// </summary>
    private async Task UploadDirectoryRecursive(
        IFileService fileService,
        UserContext user,
        string localDir,
        string remoteDir,
        SyncProgress syncProgress,
        CloudSyncTransferOptions options,
        SyncManifest? newManifest,
        ICollection<SyncFailure>? failures,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // A directory whose remote counterpart cannot be created (or listed) is
        // recorded and skipped rather than aborting the whole upload.
        if (!await TryTransferAsync(failures, remoteDir, SyncFailureOperation.CreateDirectory,
                () => CreateDirectoryAsync(remoteDir, cancellationToken), cancellationToken))
            return;

        IReadOnlyList<FileMetadata> children;
        try
        {
            children = await fileService.ListAsync(localDir, user);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            failures?.Add(new SyncFailure(localDir, SyncFailureOperation.List, exception.Message));
            return;
        }

        foreach (var item in children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string localChild = $"{localDir}/{item.Name}";
            string remoteChild = $"{remoteDir}/{item.Name}";

            // Keep the recycle bin and internal namespaces out of the remote copy
            // and the manifest, matching the top-level reconciliation guard.
            if (ShareEntryPolicy.Classify(localChild).Kind != ShareEntryKind.Regular)
                continue;

            if (item.IsDirectory)
            {
                newManifest?.Paths.Add(localChild);
                await UploadDirectoryRecursive(
                    fileService,
                    user,
                    localChild,
                    remoteChild,
                    syncProgress,
                    options,
                    newManifest,
                    failures,
                    cancellationToken);
            }
            else
            {
                if (options.ShouldSkip(item.Name, item.Size))
                    continue;

                if (await PushFileAsync(fileService, user, localChild, remoteChild,
                        item.Name, item.ModifiedAt, item.Size, options, syncProgress,
                        failures, cancellationToken))
                    newManifest?.Paths.Add(localChild);
            }
        }
    }
    
    private sealed class SyncProgress
    {
        public long TotalBytes { get; init; }
        public long TransferredBytes { get; set; }

        public Action<string, int>? Report { get; init; }

        public void Update(string text)
        {
            if (Report == null)
                return;

            int percent = TotalBytes == 0
                ? 100
                : (int)Math.Clamp(TransferredBytes * 100 / TotalBytes, 0, 100);

            Report(text, percent);
        }
    }

}

/// <summary>Which side of a sync a directory-missing failure came from.</summary>
public enum SyncEndpoint
{
    Local,
    Remote
}

/// <summary>
/// A sync endpoint directory no longer exists. Carries which side failed so the
/// health surface can show an explicit "local folder removed" / "remote folder
/// removed" message instead of a generic sync failure.
/// </summary>
public sealed class SyncDirectoryMissingException(SyncEndpoint endpoint, Exception? innerException = null)
    : Exception(
        endpoint == SyncEndpoint.Local
            ? "The local sync directory no longer exists."
            : "The remote sync directory no longer exists.",
        innerException)
{
    public SyncEndpoint Endpoint { get; } = endpoint;
}

/// <summary>
/// Forward-only write stream that funnels a download straight into an
/// <see cref="IFileSession"/> at increasing offsets, so a pulled file is written
/// to local storage once with no intermediate buffer. It never disposes the
/// session: the caller owns the session's lifetime (and its close-time hooks).
/// </summary>
internal sealed class FileSessionWriteStream(IFileSession session) : Stream
{
    private long _position;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _position;
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty) return;
        await session.WriteAsync(_position, buffer, cancellationToken);
        _position += buffer.Length;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task FlushAsync(CancellationToken cancellationToken)
        => session.FlushAsync(cancellationToken).AsTask();

    public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
