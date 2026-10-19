using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Storage;
using Kaimo_File_Server.Infrastructure.Clouds;

namespace Kaimo_File_Server.Core.Services.DataServices;

// File transfers (push/pull), transient-fault retry, local-only tree upload and progress.
internal sealed partial class CloudSyncEngine
{
    /// <summary>
    /// Uploads one local file to the remote endpoint with a transient-fault retry.
    /// Returns true only when the upload succeeded; a definitive failure is
    /// recorded in the failure sink and the caller continues.
    /// </summary>
    private async Task<bool> PushFileAsync(
        string localChild,
        string remoteChild,
        string displayName,
        DateTime modifiedAt,
        long size)
    {
        _progress.Update($"Pushing {displayName}...");
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
            await _remote.UploadAsync(remoteChild, stream, modifiedAt, _cancellationToken);
        }) && !vanished;

        if (ok)
        {
            _progress.TransferredBytes += size;
            _progress.Update($"Pushing {displayName}...");
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
        string localChild,
        string remoteChild,
        string displayName,
        DateTime modifiedAt,
        long size)
    {
        _progress.Update($"Pulling {displayName}...");
        bool vanished = false;
        bool ok = await TryTransferAsync(localChild, SyncFailureOperation.Download, async () =>
        {
            if (!await LocalParentExistsAsync(localChild))
            {
                vanished = true;
                return;
            }

            // Open a write handle and stream the download directly into it. The
            // session's dispose runs the same ownership/change-log and search-index
            // hooks as WriteFileAsync. Versioning is skipped: the remote copy is the
            // history, and a snapshot would store every pulled byte a second time.
            var open = await _local.OpenAsync(
                localChild, OpenMode.CreateOrTruncate, AccessIntent.Write, ShareIntent.None,
                _user, _cancellationToken);
            await using var session = open.Session;
            session.SkipVersioning();
            await using (var sink = _options.LimitDownload(new FileSessionWriteStream(session)))
            {
                await _remote.DownloadAsync(remoteChild, sink, _cancellationToken);
                await sink.FlushAsync(_cancellationToken);
            }
            _cancellationToken.ThrowIfCancellationRequested();
            // Stamp the source modification time so pull change-detection keeps
            // working on the next run (mirrors the former SetModifiedAtAsync call).
            // atime = mtime: providers expose no access time, and the copy was not opened since.
            await session.SetTimesAsync(new FileTimes(null, modifiedAt, modifiedAt), _cancellationToken);
        }) && !vanished;

        if (ok)
        {
            _progress.TransferredBytes += size;
            _progress.Update($"Pulling {displayName}...");
        }
        return ok;
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
        DateTime localModifiedAt)
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
                await UploadDirectoryRecursiveAsync(localChild, remoteChild, item.ModifiedAt);
            }
            else
            {
                if (_options.ShouldSkip(item.Name, item.Size))
                    continue;

                if (await PushFileAsync(localChild, remoteChild, item.Name, item.ModifiedAt, item.Size))
                    _newManifest.Paths.Add(localChild);
            }
        }

        // This pass wrote the whole folder remotely: give it the local folder's time.
        try
        {
            if (localModifiedAt > DateTime.MinValue)
                await _remote.TrySetDirectoryModifiedTimeAsync(remoteDir, localModifiedAt, _cancellationToken);
        }
        catch (Exception) when (!_cancellationToken.IsCancellationRequested)
        {
            // Best-effort: the remote folder then just keeps the upload time.
        }
    }

    private sealed class SyncProgress
    {
        public long TotalBytes { get; set; }
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
