using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Storage;

namespace Kaimo_File_Server.Web.Services;

/// <summary>Members with no meaning on remote storage: no versions, no snapshots, no local paths.</summary>
public sealed partial class RemoteMountFileService
{
    private const string LocalOnly = "Mounted folders are reached over REST and WebDAV only.";

    public Task<List<FileVersion>> GetFileVersionsAsync(string path, UserContext user) => Task.FromResult(new List<FileVersion>());

    public Task<List<DateTime>> GetSnapshotTimestampsAsync(UserContext user) => Task.FromResult(new List<DateTime>());

    public Task<List<DateTime>> GetFolderSnapshotTimestampsAsync(string folderPath, UserContext user) => Task.FromResult(new List<DateTime>());

    public Task<List<FileVersion>> GetFolderSnapshotAsync(string folderPath, DateTime asOfUtc, UserContext user) => Task.FromResult(new List<FileVersion>());

    // There are no versions, so any requested one does not exist (REST version-content answers 404).
    public Task<Stream> ReadFileVersionAsync(string path, DateTime snapshotTimestampUtc, UserContext user)
        => throw new FileNotFoundException("Mounted folders keep no versions.");

    public Task RestoreFileVersionAsync(string path, DateTime snapshotTimestampUtc, UserContext user)
        => throw new FileNotFoundException("Mounted folders keep no versions.");

    public Task UnzipAsync(string zipPath, string targetPath, UserContext user)
        => throw new UnauthorizedAccessException("Mounted folders do not support archive operations.");

    public Task ArchiveAsync(List<string> sourcePaths, string targetPath, string format, UserContext user)
        => throw new UnauthorizedAccessException("Mounted folders do not support archive operations.");

    public string ToAbsolutePath(string path) => throw new NotSupportedException(LocalOnly);

    public Task<FileOpenResult> OpenAsync(string path, OpenMode mode, AccessIntent intent, ShareIntent share, UserContext user, CancellationToken ct = default)
        => throw new NotSupportedException(LocalOnly);

    // Sync pulls only ever target local shares.
    public Task ReplaceFileAsync(string path, Func<Stream, CancellationToken, Task> writeContent, UserContext user, FileTimes sourceTimes, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(LocalOnly);

    public Task<IFileSession> OpenSnapshotAsync(string realPath, DateTime snapshotTimestampUtc, UserContext user, CancellationToken ct = default)
        => throw new NotSupportedException(LocalOnly);

    public Task OnFileCreated(string absolutePath, Task<Stream> fileData) => throw new NotSupportedException(LocalOnly);

    public Task onDirectoryCreated(string absolutePath) => throw new NotSupportedException(LocalOnly);

    public Task onFileDeleted(string absolutePath) => throw new NotSupportedException(LocalOnly);

    public Task onDirectoryDeleted(string absolutePath) => throw new NotSupportedException(LocalOnly);

    public Task NotifyExternalCloseAsync(string path, UserContext user, Func<Task<Stream>> openCapturedContent) => throw new NotSupportedException(LocalOnly);

    public Task NotifyExternalMkdirAsync(string path, UserContext user) => throw new NotSupportedException(LocalOnly);

    public Task NotifyExternalDeleteAsync(string path, bool isDirectory) => throw new NotSupportedException(LocalOnly);

    public Task NotifyExternalRenameAsync(string oldPath, string newPath, bool isDirectory, Guid sambaLifecycleEventId) => throw new NotSupportedException(LocalOnly);
}
