using Kaimo_File_Server.Core.Domain.Identity;
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

    /// <summary>
    /// Best-effort: stamps a remote directory's modification time after the sync wrote its
    /// content, so the folder keeps the source time. Providers that cannot set it keep this
    /// no-op default.
    /// </summary>
    Task TrySetDirectoryModifiedTimeAsync(
        string path, DateTime modifiedUtc, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

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
    /// The manifest of paths present on both endpoints after this run. Persist it:
    /// two-way uses it as the delete-propagation baseline, the file browser to tell
    /// remote-backed items from local-only ones.
    /// </returns>
    /// <param name="failures">
    /// Optional sink for per-item failures. When supplied, a single file that
    /// cannot be transferred is recorded here and the run continues with the
    /// next item instead of aborting; the caller reports the collected list
    /// after the run. A missing configured root directory still fails the whole
    /// run (it is a configuration error, not a per-item problem).
    /// </param>
    Task<SyncManifest?> SyncAsync(
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
        => new CloudSyncEngine(this, fileService, user, mode, transferOptions, previousManifest,
                reportProgress, failures, cancellationToken)
            .RunAsync(remotePath, localPath);
}
