using System.Security.Cryptography;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Web.Helpers;

namespace Kaimo_File_Server.Web.Components.ViewModels;

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
