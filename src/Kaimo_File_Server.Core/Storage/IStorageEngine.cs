using Kaimo_File_Server.Core.Domain;

namespace Kaimo_File_Server.Core.Storage
{
    public interface IStorageEngine
    {
        Task<Stream> ReadAsync(string path);
        /// <summary>
        /// Same as <see cref="ReadAsync(string)"/>; with <paramref name="preserveAccessTime"/> the
        /// file's last-access time is restored when the stream is disposed, so server-internal
        /// reads (search indexing, versioning, sync transfers) do not count as a user access.
        /// </summary>
        Task<Stream> ReadAsync(string path, bool preserveAccessTime);
        /// <summary>
        /// Replaces the destination atomically after the complete input stream has
        /// been persisted. A failed or cancelled write must leave an existing
        /// destination unchanged and must not publish a partial new file.
        /// </summary>
        Task WriteAsync(string path, Stream data, CancellationToken cancellationToken = default);
        /// <summary>
        /// Same guarantee for content that is pushed instead of read: <paramref name="writeContent"/>
        /// writes into the unpublished file and must not dispose the stream. The destination is
        /// replaced only when it completes; if it throws, the destination stays unchanged.
        /// Not forced to disk: meant for many files that exist elsewhere as well (sync pulls), where
        /// one fsync per file would dominate the run.
        /// </summary>
        Task WriteAsync(string path, Func<Stream, CancellationToken, Task> writeContent, CancellationToken cancellationToken = default);
        /// <summary>
        /// Removes temporary upload files directly in <paramref name="directoryPath"/> that were
        /// last written before <paramref name="modifiedBeforeUtc"/>: leftovers of a crashed process.
        /// </summary>
        Task DeleteAbandonedUploadsAsync(string directoryPath, DateTime modifiedBeforeUtc) => Task.CompletedTask;
        Task CreateDirectory(string path);
        Task DeleteAsync(string path);
        /// <summary>
        /// Moves a file/directory. On a name collision at the target a timestamp
        /// suffix is appended, so the ACTUAL relative target path may differ from
        /// <paramref name="newPath"/>. That actual path is returned — callers that
        /// keep side tables in sync (ACLs, search index) MUST use it, not the
        /// requested path.
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
        /// <summary>
        /// Stamps a source modification time on a file or directory and sets its last-access
        /// time to the same value, so a copied item looks untouched since that modification.
        /// </summary>
        Task SetModifiedDateAsync(string path, DateTime time);
        /// <summary>
        /// Stamps a source creation time on a file or directory, stored so that every transport
        /// (web, SMB) reports it. Throws <see cref="IOException"/> when the item does not exist.
        /// Engines that cannot store a creation time keep the default no-op.
        /// </summary>
        Task SetCreatedDateAsync(string path, DateTime time) => Task.CompletedTask;
        /// <summary>
        /// Stamps a source last-access time on a file or directory without touching its
        /// modification time. Throws <see cref="IOException"/> when the item does not exist.
        /// Engines without access times keep the default no-op.
        /// </summary>
        Task SetAccessedDateAsync(string path, DateTime time) => Task.CompletedTask;
        public string ToAbsolutePath(string shareRelativePath);
        /// <summary>
        /// Calculates the total size of all files within a directory (recursive).
        /// Returns 0 if the directory doesn't exist or is empty.
        /// </summary>
        Task<long> GetDirectorySizeAsync(string directoryPath);

        Task<IStorageHandle> OpenAsync(
            string path,
            OpenMode mode,
            AccessIntent intent,
            ShareIntent share,
            CancellationToken ct = default);

        Task<bool> ExistsAsync(string path);
    }
}
