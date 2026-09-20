using System.Security.Cryptography;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Web.Helpers;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>Per-entry sync state that drives which emblem the file browser shows.</summary>
public enum SyncItemState
{
    /// <summary>Item existed at/before the last successful run — considered in sync.</summary>
    Synced,
    /// <summary>Changed since the last run under a push/two-way sync — upload still pending.</summary>
    PendingUpload,
    /// <summary>Changed since the last run under a pull-only sync — will never be uploaded.</summary>
    PullBlocked
}

/// <summary>
/// Marks a browsed entry that lives at (or beneath) the local destination folder of
/// a sync. Drives the sync emblem and its tooltip in the file browser.
/// </summary>
public sealed record SyncFolderMarker(SyncMode Mode, string SyncName, SyncItemState State = SyncItemState.Synced);

/// <summary>Decides a browsed entry's sync state from timestamps and direction.</summary>
public static class SyncItemStateEvaluator
{
    /// <summary>
    /// Decides an entry's emblem. Applies to files and folders alike (a folder's write time
    /// moves when its direct children change).
    ///
    /// Pull is authoritative from the remote: a run never uploads a local item, so once the
    /// converged manifest is known, "synced" is decided purely by <paramref name="isRemoteBacked"/>
    /// (present in the manifest) — a local-only item stays flagged across runs until removed or,
    /// with delete propagation on, deleted. <paramref name="isRemoteBacked"/> is <c>null</c> when
    /// no manifest exists yet (a pull that has not run under manifest tracking); the timestamp
    /// heuristic below then stands in until the first run records one.
    ///
    /// ponytail: push/two-way have no manifest and keep the mtime heuristic. A successful run
    /// really does upload the item, so "last write at/before the last success" == synced holds;
    /// anything newer (or before any success) is still pending upload. Both times must be UTC.
    /// </summary>
    public static SyncItemState Evaluate(
        DateTime modifiedAtUtc, DateTime? lastSuccessfulRunAtUtc, SyncMode mode, bool? isRemoteBacked)
    {
        // Pull never uploads, so "local-only" is authoritative only from the manifest.
        // Without one (sync not yet converged), timestamps cannot tell a just-pulled item
        // from a genuinely local-only one — so assume in sync rather than warn on every
        // entry. Only an explicit manifest miss (isRemoteBacked == false) blocks.
        if (mode == SyncMode.Pull)
            return isRemoteBacked == false ? SyncItemState.PullBlocked : SyncItemState.Synced;

        if (lastSuccessfulRunAtUtc is not null && modifiedAtUtc <= lastSuccessfulRunAtUtc)
            return SyncItemState.Synced;

        return SyncItemState.PendingUpload;
    }
}

/// <summary>
/// Contract consumed by the reusable file-browser UI. Core browsing operations are
/// mandatory; local-only features have safe defaults and are advertised through
/// <see cref="Capabilities"/>. A Cloud Access view model can therefore reuse the UI
/// without inheriting local filesystem, ACL, versioning, or Samba assumptions.
/// </summary>
public interface IFileBrowserViewModel
{
    event Action? OnStateChanged;

    BrowserCapabilities Capabilities { get; }
    BrowserShareInfo? CurrentBrowserShare { get; }

    /// <summary>Local backing definition, when the backend is a local Kaimo share.</summary>
    ShareDefinition? CurrentShare => null;

    List<FileMetadata> Items { get; }
    string CurrentPath { get; }
    bool IsLoading { get; }
    string? ErrorMessage { get; }
    bool CanManageAcls => false;
    bool CanManageSyncs => false;

    /// <summary>
    /// True when the current backend is a virtual (Cloud Access) share whose root-level
    /// ACLs the actor may manage. Local backends keep the safe default; the file browser
    /// uses it to offer the "manage permissions" action at the virtual share's root.
    /// </summary>
    bool CanManageVirtualShareAcls => false;

    /// <summary>
    /// Returns the sync marker for an entry when it is, or lives beneath, the local
    /// destination folder of a sync; <c>null</c> otherwise. Backends without local
    /// syncs (Cloud Access, remote) keep the safe default.
    /// </summary>
    SyncFolderMarker? GetSyncMarker(FileMetadata entry) => null;
    IEnumerable<FileMetadata> Directories { get; }
    IEnumerable<FileMetadata> Files { get; }
    bool HasParent { get; }
    string ParentPath { get; }
    List<(string Name, string FullPath)> Breadcrumbs { get; }

    Task LoadShareAsync(string shareKey, string subPath = "");
    Task RefreshCurrentDirectoryAsync();
    Task<OperationResult> CreateFolderAsync(string folderName);
    Task CreateFolderAtAsync(string path);
    Task<OperationResult> DeleteAsync(FileMetadata item);
    Task<OperationResult> RenameAsync(FileMetadata item, string newName);
    Task<OperationResult> MoveAsync(FileMetadata item, string destinationPath);
    Task<List<FileMetadata>> ListDirectoryAsync(string directoryPath);
    Task CopyAsync(FileMetadata item, string targetPath, CancellationToken cancellationToken);
    Task<(byte[] Data, string ContentType, PreviewKind Kind)?> ReadFileForPreviewAsync(FileMetadata file);
    Task<OperationResult> UploadFileAsync(
        string fileName,
        Stream fileStream,
        CancellationToken cancellationToken = default);
    long GetMaxPreviewSizeBytes();
    long GetMaxUploadSizeBytes();
    string ShareRelativeOf(FileMetadata item);

    Task<string?> GetDownloadUrlAsync(FileMetadata file)
        => Task.FromResult<string?>(null);

    /// <summary>
    /// Streams the file through the given hash algorithm and returns the hex digest,
    /// or null when the backend cannot read file content. Not size-capped — the file
    /// is hashed in a streaming pass, never buffered whole.
    /// </summary>
    Task<string?> ComputeFileHashAsync(
        FileMetadata file, HashAlgorithmName algorithm, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);

    Task LoadAclCountsAsync() => Task.CompletedTask;
    int GetAclCount(string path) => 0;
    long? GetDirectorySize(FileMetadata directory) => null;
    /// <summary>
    /// Calculates a directory's complete size when the backend cannot provide it
    /// from its listing metadata (for example a virtual provider).
    /// </summary>
    Task<long?> CalculateDirectorySizeAsync(FileMetadata directory, CancellationToken cancellationToken = default)
        => Task.FromResult(GetDirectorySize(directory));

    Task<List<FileVersion>> GetFileVersionsAsync(FileMetadata file)
        => Task.FromResult(new List<FileVersion>());

    Task<List<DateTime>> GetFolderSnapshotTimestampsAsync(FileMetadata folder)
        => Task.FromResult(new List<DateTime>());

    Task<List<FileVersion>> GetFolderSnapshotAsync(FileMetadata folder, DateTime asOfUtc)
        => Task.FromResult(new List<FileVersion>());

    Task<(byte[] Data, string ContentType, PreviewKind Kind)?> ReadVersionForPreviewAsync(
        string shareRelativePath,
        DateTime snapshotTimestampUtc,
        string fileName,
        long size)
        => Task.FromResult<(byte[] Data, string ContentType, PreviewKind Kind)?>(null);

    Task<byte[]?> ReadVersionBytesAsync(string shareRelativePath, DateTime snapshotTimestampUtc)
        => Task.FromResult<byte[]?>(null);

    Task<OperationResult> RestoreVersionAsync(FileMetadata file, DateTime snapshotTimestampUtc)
        => Task.FromResult(OperationResult.Fail("File versions are not supported by this backend."));

    Task<(Guid Id, Guid OwnerId)?> GetPersistedMetadataAsync(FileMetadata item)
        => Task.FromResult<(Guid Id, Guid OwnerId)?>(null);

    Task<string?> GetOwnerDisplayNameAsync(Guid ownerId)
        => Task.FromResult<string?>(null);

    Task<OperationResult> ArchiveAsync(List<FileMetadata> items, string format)
        => Task.FromResult(OperationResult.Fail("Archives are not supported by this backend."));

    Task<OperationResult> UnzipAsync(FileMetadata file)
        => Task.FromResult(OperationResult.Fail("Extracting archives is not supported by this backend."));
}
