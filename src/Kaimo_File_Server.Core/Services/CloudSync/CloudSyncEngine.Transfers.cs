using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Core.Services.Notifications;
using Kaimo_File_Server.Core.Storage;

namespace Kaimo_File_Server.Core.Services.CloudSync;

// File transfers (push/pull), transient-fault retry, local-only tree upload and progress.
internal sealed partial class CloudSyncEngine
{
    /// <summary>
    /// Uploads one local file to the remote endpoint with a transient-fault retry.
    /// Returns true only when the upload succeeded; a definitive failure is
    /// recorded in the failure sink and the caller continues.
    /// </summary>
    /// <param name="createdAt">Creation time to send, or <c>null</c> to keep the remote one.</param>
    private async Task<bool> PushFileAsync(
        string localChild,
        string remoteChild,
        string displayName,
        DateTime modifiedAt,
        DateTime? createdAt,
        long size)
    {
        string action = string.Format(SyncProgress.Text("Web_Jobs_CloudSync_Uploading", "Uploading {0}"), displayName);
        _progress.Update(action);
        bool vanished = false;
        bool ok = await TryTransferAsync(localChild, SyncFailureOperation.Upload, async () =>
        {
            Stream source;
            try
            {
                // A sync transfer is not a user access: keep the source's last-access time.
                source = await _local.ReadFileAsync(localChild, _user, preserveAccessTime: true);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                // Deleted locally after its directory was listed. Not a failure:
                // the next run reconciles the deletion against the manifest.
                vanished = true;
                return;
            }
            await using var stream = _options.LimitUpload(source);
            await _remote.UploadAsync(remoteChild, stream, modifiedAt, createdAt, _cancellationToken);
        }) && !vanished;

        _progress.FileDone(size);
        _progress.Update(action);
        return ok;
    }

    /// <summary>
    /// Downloads one remote file to the local endpoint with a transient-fault
    /// retry. The payload streams into an unpublished file next to the target, so
    /// it is written to local storage exactly once while staying bounded in memory,
    /// and a failed download leaves the local file untouched: a truncated copy with
    /// the current time would count as the newer side and be pushed over the intact
    /// remote file. Returns true only when the download succeeded.
    /// </summary>
    /// <param name="createdAt">
    /// Creation time to stamp, or <c>null</c> to keep the local one (an overwrite whose
    /// provider reports no creation time, or a later one).
    /// </param>
    /// <param name="listedLocal">
    /// The local file as listed when this pull was decided, or <c>null</c> when there was none.
    /// The pull is dropped if that no longer holds once the download has finished.
    /// </param>
    private async Task<bool> PullFileAsync(
        string localChild,
        string remoteChild,
        string displayName,
        DateTime modifiedAt,
        DateTime? createdAt,
        long size,
        FileMetadata? listedLocal)
    {
        string action = string.Format(SyncProgress.Text("Web_Jobs_CloudSync_Downloading", "Downloading {0}"), displayName);
        _progress.Update(action);
        bool vanished = false;
        bool ok = await TryTransferAsync(localChild, SyncFailureOperation.Download, async () =>
        {
            if (!await LocalParentExistsAsync(localChild))
            {
                vanished = true;
                return;
            }

            // The source modification time keeps pull change-detection working on the next run.
            // atime = mtime: providers expose no access time, and the copy was not opened since.
            // Without a creation time the file would show the time of the sync run as created.
            try
            {
                await _local.ReplaceFileAsync(localChild, async (file, token) =>
                {
                    // Not disposed: the stream belongs to the storage, which still has to flush it.
                    Stream sink = _options.LimitDownload(file);
                    await _remote.DownloadAsync(remoteChild, sink, token);
                    await sink.FlushAsync(token);
                    await ThrowIfLocalChangedAsync(localChild, listedLocal);
                }, _user, new FileTimes(createdAt, modifiedAt, modifiedAt), _cancellationToken);
            }
            catch (LocalFileChangedException)
            {
                // Not a failure: the next run reconciles the file in its new state.
                vanished = true;
            }
        }) && !vanished;

        _progress.FileDone(size);
        _progress.Update(action);
        return ok;
    }

    /// <summary>
    /// Aborts a pull whose target changed since it was listed: a large download takes long
    /// enough for a user to save a newer version, which would be lost without a snapshot.
    /// </summary>
    // ponytail: check-then-replace leaves a tiny race; an atomic compare needs a storage-level
    // handle held across the download if that ever matters.
    private async Task ThrowIfLocalChangedAsync(string localChild, FileMetadata? listedLocal)
    {
        FileMetadata? current;
        try
        {
            current = await _local.GetMetadataAsync(localChild, _user);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            current = null;
        }

        bool unchanged = listedLocal is null
            ? current is null
            : current is { IsDirectory: false }
              && current.Size == listedLocal.Size
              && current.ModifiedAt == listedLocal.ModifiedAt;
        if (!unchanged)
            throw new LocalFileChangedException();
    }

    private sealed class LocalFileChangedException : Exception;

    /// <summary>
    /// Removes what a crashed process left in a folder this run visits. Housekeeping only:
    /// it must never affect the sync itself.
    /// </summary>
    private async Task DeleteAbandonedUploadsAsync(string localDir)
    {
        try
        {
            await _local.DeleteAbandonedUploadsAsync(localDir, _user);
        }
        catch (Exception) when (!_cancellationToken.IsCancellationRequested)
        {
            // Retried by the next run.
        }
    }

    /// <summary>
    /// Local writes recreate missing parent folders, so a folder deleted locally
    /// while a run walks the tree would be resurrected holding only the items
    /// pulled into it, and the deletion would never reach the remote. Callers
    /// skip the write when this returns false; the next run then propagates the
    /// deletion via the manifest.
    /// </summary>
    // ponytail: check-then-write leaves a tiny race; a no-create-parents write
    // mode in the storage layer would close it if it ever matters.
    private async Task<bool> LocalParentExistsAsync(string localChild)
    {
        try
        {
            return (await _local.GetMetadataAsync(ShareRelativePath.GetParent(localChild), _user)).IsDirectory;
        }
        catch (FileNotFoundException) { return false; }
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
        Kaimo_File_Server.Core.Services.ExternalStorage.RemoteItemRejectedException => false,
        // A full pool stays full: another attempt would only transfer the whole file again.
        IOException io when StorageErrors.IsDiskFull(io) => false,
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
    private async Task<bool> TryTransferAsync(
        string path,
        SyncFailureOperation operation,
        Func<Task> action)
    {
        const int maxAttempts = 3;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await action();
                return true;
            }
            catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (attempt < maxAttempts && IsTransient(exception))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt * attempt), _cancellationToken);
            }
            catch (Exception exception)
            {
                _failures?.Add(new SyncFailure(path, operation, exception.Message));
                return false;
            }
        }
    }

    /// <summary>
    /// Uploads a local-only directory tree while forwarding progress and the
    /// cancellation token into each provider call.
    /// </summary>
    private async Task UploadDirectoryRecursiveAsync(
        string localDir,
        string remoteDir,
        DateTime localModifiedAt,
        DateTime? localCreatedAt)
    {
        _cancellationToken.ThrowIfCancellationRequested();

        // A directory whose remote counterpart cannot be created (or listed) is
        // recorded and skipped rather than aborting the whole upload.
        if (!await TryTransferAsync(remoteDir, SyncFailureOperation.CreateDirectory,
                () => _remote.CreateDirectoryAsync(remoteDir, _cancellationToken)))
            return;

        IReadOnlyList<FileMetadata> children;
        try
        {
            children = await _local.ListAsync(localDir, _user);
        }
        catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DirectoryNotFoundException)
        {
            // Deleted locally mid-run; the caller already recorded it in the
            // manifest, so the next run removes the just-created remote folder.
            return;
        }
        catch (Exception exception)
        {
            _failures?.Add(new SyncFailure(localDir, SyncFailureOperation.List, exception.Message));
            return;
        }

        foreach (var item in children)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            string localChild = $"{localDir}/{item.Name}";
            string remoteChild = $"{remoteDir}/{item.Name}";

            // Keep the recycle bin and internal namespaces out of the remote copy
            // and the manifest, matching the top-level reconciliation guard.
            if (ShareEntryPolicy.Classify(localChild).Kind != ShareEntryKind.Regular)
                continue;

            if (item.IsDirectory)
            {
                _newManifest.Paths.Add(localChild);
                await UploadDirectoryRecursiveAsync(localChild, remoteChild, item.ModifiedAt, KnownCreatedAt(item));
            }
            else
            {
                if (_options.ShouldSkip(item.Name, item.Size))
                {
                    _progress.FileDone(item.Size);
                    continue;
                }

                if (await PushFileAsync(localChild, remoteChild, item.Name, item.ModifiedAt,
                        KnownCreatedAt(item) ?? item.ModifiedAt, item.Size))
                    _newManifest.Paths.Add(localChild);
            }
        }

        // This pass wrote the whole folder remotely: give it the local folder's times
        // (the modification time stands in for an unknown creation time).
        try
        {
            if (localModifiedAt > DateTime.MinValue)
                await _remote.TrySetDirectoryTimesAsync(
                    remoteDir, localModifiedAt, localCreatedAt ?? localModifiedAt, _cancellationToken);
        }
        catch (Exception) when (!_cancellationToken.IsCancellationRequested)
        {
            // Best-effort: the remote folder then just keeps the upload time.
        }
    }

    /// <summary>
    /// Progress of one run. The percentage counts every file the run has dealt with
    /// (transferred, unchanged, skipped or failed) against the pre-scanned total, so a
    /// mostly converged tree advances while it is compared instead of sitting at 0%.
    /// Messages are localized with the culture of the thread that runs the sync.
    /// </summary>
    private sealed class SyncProgress
    {
        public long TotalBytes { get; set; }
        public long ProcessedBytes { get; private set; }
        public int CheckedFiles { get; private set; }
        public int ScannedFiles { get; private set; }
        public long ScannedBytes { get; private set; }

        public Action<string, int>? Report { get; init; }

        public static string Text(string key, string fallback)
            => Resources.ResourceManager.GetString(key) ?? fallback;

        /// <summary>Counts one file as dealt with, whatever its outcome.</summary>
        public void FileDone(long size)
        {
            CheckedFiles++;
            ProcessedBytes += size;
        }

        /// <summary>Adds one remote listing to the pre-scan counters and reports them.</summary>
        public void Scanned(IEnumerable<CloudItemMeta> items)
        {
            foreach (var item in items.Where(i => !i.IsDirectory))
            {
                ScannedFiles++;
                ScannedBytes += item.Size;
            }
            Report?.Invoke(string.Format(
                Text("Web_Jobs_CloudSync_RemoteScan", "Scanning remote folder… Files found: {0} ({1})"),
                ScannedFiles, NotificationEvents.FormatSize(ScannedBytes)), 0);
        }

        /// <summary>Reports a pre-scan step; the total is not known yet, so it stays at 0%.</summary>
        public void ReportPhase(string text) => Report?.Invoke(text, 0);

        public void Update(string action)
        {
            if (Report == null)
                return;

            int percent = TotalBytes == 0
                ? 100
                : (int)Math.Clamp(ProcessedBytes * 100 / TotalBytes, 0, 100);

            Report(string.Format(
                Text("Web_Jobs_CloudSync_Summary", "{0} · Files checked: {1} · {2} of {3}"),
                action, CheckedFiles,
                NotificationEvents.FormatSize(ProcessedBytes), NotificationEvents.FormatSize(TotalBytes)), percent);
        }
    }
}
