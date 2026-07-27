using Kaimo_File_Server.Core.Domain;

namespace Kaimo_File_Server.Core.Storage
{
    public interface IStorageEngine
    {
        Task<Stream> ReadAsync(string path);
        Task WriteAsync(string path, Stream data, CancellationToken cancellationToken = default);
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