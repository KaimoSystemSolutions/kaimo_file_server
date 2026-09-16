using Kaimo_File_Server.Core.Domain;

namespace Kaimo_File_Server.Core.Storage
{
    public interface IStorageEngine
    {
        Task<Stream> ReadAsync(string path);
        /// <summary>
        /// Replaces the destination atomically after the complete input stream has
        /// been persisted. A failed or cancelled write must leave an existing
        /// destination unchanged and must not publish a partial new file.
        /// </summary>
        Task WriteAsync(string path, Stream data, CancellationToken cancellationToken = default);
        Task CreateDirectory(string path);
        /// <summary>
        /// Deletes the file or directory at <paramref name="path"/> (directories
        /// recursively). By default a missing path is an error and throws
        /// <see cref="FileNotFoundException"/>, so a caller cannot silently write ACL,
        /// ownership and change-log rows for a path that holds nothing. Pass
        /// <paramref name="ignoreMissing"/> for the callers that genuinely want an
        /// idempotent delete.
        /// </summary>
        Task DeleteAsync(string path, bool ignoreMissing = false);
        /// <summary>
        /// Moves a file/directory. On a name collision at the target a timestamp
        /// suffix is appended, so the ACTUAL relative target path may differ from
        /// <paramref name="newPath"/>. That actual path is returned — callers that
        /// keep side tables in sync (ACLs, search index) MUST use it, not the
        /// requested path. A missing source path throws <see cref="FileNotFoundException"/>.
        /// </summary>
        Task<string> MoveAsync(string oldPath, string newPath);
        Task<FileMetadata> GetMetadataAsync(string path);
        Task<List<FileMetadata>> ListAsync(string directoryPath);
        Task CreateDirectoryAsync(string path);
        Task<bool> IsDirectoryAsync(string path);
        Task RenameFileAsync(string oldPath, string newPath);
        Task RenameDirectoryAsync(string oldDirPath, string newDirPath);
        Task UnzipAsync(string zipPath, string targetPath);
        Task ArchiveAsync(List<string> sourcePaths, string targetPath, string format);
        Task SetModifiedDateAsync(string path, DateTime time);
        public string ToAbsolutePath(string shareRelativePath);
        /// <summary>
        /// Calculates the total size of all files within a directory (recursive).
        /// Returns 0 if the directory doesn't exist or is empty. The walk is honest
        /// synchronous syscall work run on a thread-pool thread and observes
        /// <paramref name="cancellationToken"/>, so a re-navigation can abandon it.
        /// </summary>
        Task<long> GetDirectorySizeAsync(string directoryPath, CancellationToken cancellationToken = default);

        Task<IStorageHandle> OpenAsync(
            string path,
            OpenMode mode,
            AccessIntent intent,
            ShareIntent share,
            CancellationToken ct = default);

        Task<bool> ExistsAsync(string path);
    }
}
