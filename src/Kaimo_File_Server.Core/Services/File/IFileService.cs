using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Storage;

namespace Kaimo_File_Server.Core.Services.File
{
    /// <summary>
    /// Central abstraction for all file operations with permission checks.
    /// 
    /// Every transport (SMB, HTTP, NFS) depends on this interface, not the concrete class.
    /// This enables:
    ///   - Unit testing with mocked file operations
    ///   - Alternative implementations (e.g. cloud storage backend)
    ///   - Decorator pattern for cross-cutting concerns (logging, caching, auditing)
    /// </summary>
    public interface IFileService
    {
        // ------------ Permission Checks ------------
        Task<bool> CanReadAsync(string path, UserContext user);
        Task<bool> CanWriteAsync(string path, UserContext user);
        Task<bool> CanCreateAsync(string path, UserContext user);
        Task<bool> CanDeleteAsync(string path, UserContext user);
        Task<bool> CanListAsync(string path, UserContext user);

        // ------------ Full Operations ------------
        string ToAbsolutePath(string path);
        Task<Stream> ReadFileAsync(string path, UserContext user);
        
        
        // expects a local path inside the share. so if the storage path was
        // /storage/general/folder/file.txt
        // it would expect folder/file.txt
        Task WriteFileAsync(string path, Stream data, UserContext user, CancellationToken cancellationToken = default);
        Task CreateFileAsync(string path, UserContext user);
        Task CreateDirectoryAsync(string path, UserContext user);
        Task DeleteFileAsync(string path, UserContext user, bool isRecycleEnabled);
        Task<FileMetadata> GetMetadataAsync(string path, UserContext user);
        Task<List<FileMetadata>> ListAsync(string directoryPath, UserContext user);
        Task RenameAsync(string oldPath, string newPath, UserContext user);
        Task<long> GetDirectorySizeAsync(string relativePath, UserContext userContext);
        Task UnzipAsync(string zipPath, string targetPath, UserContext user);
        Task ArchiveAsync(List<string> sourcePaths, string targetPath, string format, UserContext user);
        
        // ------------ Hooks ------------

        Task OnFileCreated(string absolutePath, Task<Stream> fileData);
        
        Task onDirectoryCreated(string absolutePath);

        Task onFileDeleted(string absolutePath);

        Task onDirectoryDeleted(string absolutePath);

        // ---- External-writer close hooks (Samba VFS direct I/O) ----
        // Samba performs the raw I/O natively, then calls these so the same
        // cross-cutting effects as FileSession.DisposeAsync run: versioning,
        // search indexing, ownership. Best-effort (failures logged, not thrown).

        /// <summary>A file was written+closed externally: snapshot a version, index it, stamp owner.</summary>
        Task NotifyExternalCloseAsync(string path, UserContext user);

        /// <summary>A directory was created externally: index it and stamp owner.</summary>
        Task NotifyExternalMkdirAsync(string path, UserContext user);

        /// <summary>A file/directory was deleted externally: remove it from the search index.</summary>
        Task NotifyExternalDeleteAsync(string path, bool isDirectory);

        /// <summary>A file/directory was renamed externally: realign ACL records and the search index.</summary>
        Task NotifyExternalRenameAsync(string oldPath, string newPath, bool isDirectory);

        /// <summary>
        /// Returns the subset of paths the user has ListReadData permission on.
        /// Single DB round trip via batch ACL evaluation.
        ///
        /// Used by SMB QueryDirectory to filter listings without N+1 queries.
        /// </summary>
        Task<HashSet<string>> FilterReadablePathsAsync(
            IReadOnlyList<(string relativePath, bool isDirectory)> items,
            UserContext user);

        Task<FileOpenResult> OpenAsync(
            string path,
            OpenMode mode,
            AccessIntent intent,
            ShareIntent share,
            UserContext user,
            CancellationToken ct = default);

        Task<IFileSession> OpenSnapshotAsync(
            string realPath,
            DateTime snapshotTimestampUtc,
            UserContext user,
            CancellationToken ct = default);

        Task<List<DateTime>> GetSnapshotTimestampsAsync(UserContext user);

        // ------------ Versioning (web UI) ------------

        /// <summary>
        /// Lists all stored versions of a file (newest first). Requires read access.
        /// Returns an empty list when versioning is not configured.
        /// </summary>
        Task<List<FileVersion>> GetFileVersionsAsync(string path, UserContext user);

        /// <summary>
        /// Reads the content of a specific version of a file. Requires read access.
        /// The returned stream is seekable; large versions may be backed by a
        /// delete-on-close temporary file instead of managed memory.
        /// </summary>
        Task<Stream> ReadFileVersionAsync(string path, DateTime snapshotTimestampUtc, UserContext user);

        /// <summary>
        /// Restores a file to the content of an earlier version. The current content
        /// is snapshotted as its own version first, so a restore is itself undoable.
        /// Requires write access.
        /// </summary>
        Task RestoreFileVersionAsync(string path, DateTime snapshotTimestampUtc, UserContext user);

        /// <summary>
        /// Distinct snapshot timestamps of any file inside a folder (newest first).
        /// Drives the "point-in-time" picker in the web UI. Requires list access.
        /// </summary>
        Task<List<DateTime>> GetFolderSnapshotTimestampsAsync(string folderPath, UserContext user);

        /// <summary>
        /// Point-in-time state of a folder: for every versioned file the user may
        /// read, the newest version at or before <paramref name="asOfUtc"/>.
        /// Requires list access on the folder.
        /// </summary>
        Task<List<FileVersion>> GetFolderSnapshotAsync(string folderPath, DateTime asOfUtc, UserContext user);
    }
}
