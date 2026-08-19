using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Services.File;
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
    async Task<SyncManifest?> SyncAsync(
        IFileService fileService,
        UserContext user,
        string remotePath,
        string localPath,
        SyncMode mode,
        CloudSyncTransferOptions? transferOptions = null,
        SyncManifest? previousManifest = null,
        Action<string?, int>? reportProgress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long totalBytes = mode switch
        {
            SyncMode.Push => await fileService.GetDirectorySizeAsync(localPath, user),
            SyncMode.Pull => await GetDirectorySizeAsync(remotePath, cancellationToken),
            SyncMode.TwoWay => Math.Max(
                await fileService.GetDirectorySizeAsync(localPath, user),
                await GetDirectorySizeAsync(remotePath, cancellationToken)),
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
        bool buildManifest = mode == SyncMode.TwoWay;
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
            cancellationToken
            );

        return newManifest;
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
        CancellationToken cancellationToken
        )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var remoteItems = (await ListAsync(remoteDir, cancellationToken))
            .ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);

        var localItems = (await fileService.ListAsync(localDir, user))
            .ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
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
                    continue;

                // Present locally, absent remotely, and known from the last run:
                // it was deleted remotely, so remove it locally instead of
                // re-uploading it. The local delete honors the share recycle bin.
                if (previousManifest?.Contains(localChild) == true)
                {
                    syncProgress.Update($"Removing {local!.Name}...");
                    await fileService.DeleteFileAsync(localChild, user, options.HonorRecycleBin);
                    continue;
                }

                if (local!.IsDirectory)
                {
                    await CreateDirectoryAsync(remoteChild, cancellationToken);
                    newManifest?.Paths.Add(localChild);

                    await UploadDirectoryRecursive(
                        fileService,
                        user,
                        localChild,
                        remoteChild,
                        syncProgress,
                        options,
                        newManifest,
                        cancellationToken);
                }
                else
                {
                    syncProgress.Update($"Pushing {local.Name}...");

                    await using var stream = options.LimitUpload(await fileService.ReadFileAsync(localChild, user));
                    await UploadAsync(remoteChild, stream, local.ModifiedAt, cancellationToken);
                    newManifest?.Paths.Add(localChild);

                    syncProgress.TransferredBytes += local.Size;
                    syncProgress.Update($"Pushing {local.Name}...");
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
                    await DeleteAsync(remoteChild, remote.IsDirectory, cancellationToken);
                    continue;
                }

                if (remote!.IsDirectory)
                {
                    await fileService.CreateDirectoryAsync(localChild, user);
                    cancellationToken.ThrowIfCancellationRequested();
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
                        cancellationToken);
                }
                else
                {
                    syncProgress.Update($"Pulling {remote.Name}...");

                    await using var raw = new MemoryStream();
                    await using var ms = options.LimitDownload(raw);
                    await DownloadAsync(remoteChild, ms, cancellationToken);

                    ms.Position = 0;
                    cancellationToken.ThrowIfCancellationRequested();
                    await fileService.WriteFileAsync(localChild, ms, user);
                    cancellationToken.ThrowIfCancellationRequested();
                    await fileService.SetModifiedAtAsync(localChild, user, remote.ModifiedAt);
                    newManifest?.Paths.Add(localChild);

                    syncProgress.TransferredBytes += remote.Size;
                    syncProgress.Update($"Pulling {remote.Name}...");
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
                    cancellationToken);

                continue;
            }

            if (local.IsDirectory != remote.IsDirectory)
                continue;

            // Present on both sides as files: reconciled below regardless of which
            // way the newer copy flows, so it stays in the converged manifest.
            newManifest?.Paths.Add(localChild);

            //--------------------------------------------------
            // Pull
            //--------------------------------------------------
            if (mode == SyncMode.Pull)
            {
                if (CompareModifiedTime(local.ModifiedAt, remote.ModifiedAt) < 0)
                {
                    syncProgress.Update($"Pulling {remote.Name}...");

                    await using var raw = new MemoryStream();
                    await using var ms = options.LimitDownload(raw);
                    await DownloadAsync(remoteChild, ms, cancellationToken);

                    ms.Position = 0;
                    cancellationToken.ThrowIfCancellationRequested();
                    await fileService.WriteFileAsync(localChild, ms, user);
                    cancellationToken.ThrowIfCancellationRequested();
                    await fileService.SetModifiedAtAsync(localChild, user, remote.ModifiedAt);

                    syncProgress.TransferredBytes += remote.Size;
                    syncProgress.Update($"Pulling {remote.Name}...");
                }

                continue;
            }

            //--------------------------------------------------
            // Push
            //--------------------------------------------------
            if (mode == SyncMode.Push)
            {
                if (CompareModifiedTime(local.ModifiedAt, remote.ModifiedAt) > 0)
                {
                    syncProgress.Update($"Pushing {local.Name}...");

                    await using var stream = options.LimitUpload(await fileService.ReadFileAsync(localChild, user));
                    await UploadAsync(remoteChild, stream, local.ModifiedAt, cancellationToken);

                    syncProgress.TransferredBytes += local.Size;
                    syncProgress.Update($"Pushing {local.Name}...");
                }

                continue;
            }

            //--------------------------------------------------
            // Two-way
            //--------------------------------------------------
            if (CompareModifiedTime(local.ModifiedAt, remote.ModifiedAt) > 0)
            {
                syncProgress.Update($"Pushing {local.Name}...");

                await using var stream = options.LimitUpload(await fileService.ReadFileAsync(localChild, user));
                await UploadAsync(remoteChild, stream, local.ModifiedAt, cancellationToken);

                syncProgress.TransferredBytes += local.Size;
                syncProgress.Update($"Pushing {local.Name}...");
            }
            else if (CompareModifiedTime(local.ModifiedAt, remote.ModifiedAt) < 0)
            {
                syncProgress.Update($"Pulling {remote.Name}...");

                await using var raw = new MemoryStream();
                await using var ms = options.LimitDownload(raw);
                await DownloadAsync(remoteChild, ms, cancellationToken);

                ms.Position = 0;
                cancellationToken.ThrowIfCancellationRequested();
                await fileService.WriteFileAsync(localChild, ms, user);
                cancellationToken.ThrowIfCancellationRequested();
                await fileService.SetModifiedAtAsync(localChild, user, remote.ModifiedAt);
                
                syncProgress.TransferredBytes += remote.Size;
                syncProgress.Update($"Pulling {remote.Name}...");
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
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await CreateDirectoryAsync(remoteDir, cancellationToken);

        foreach (var item in await fileService.ListAsync(localDir, user))
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
                    cancellationToken);
            }
            else
            {
                syncProgress.Update($"Pushing {item.Name}");

                if (options.ShouldSkip(item.Name, item.Size))
                    continue;
                await using var stream = options.LimitUpload(await fileService.ReadFileAsync(localChild, user));
                await UploadAsync(remoteChild, stream, item.ModifiedAt, cancellationToken);
                newManifest?.Paths.Add(localChild);

                syncProgress.TransferredBytes += item.Size;
                syncProgress.Update($"Pushing {item.Name}");
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
