using Kaimo_File_Server.Core.Domain;

namespace Kaimo_File_Server.Core.Storage
{
    public interface IStorageEngine
    {
        Task<Stream> ReadAsync(string path);
        Task WriteAsync(string path, Stream data, CancellationToken cancellationToken = default);
        Task CreateDirectory(string path);
        Task DeleteAsync(string path);
        Task MoveAsync(string oldPath, string newPath);
        Task<FileMetadata> GetMetadataAsync(string path);
        Task<List<FileMetadata>> ListAsync(string directoryPath);
        Task CreateDirectoryAsync(string path);
        Task<bool> IsDirectoryAsync(string path);
        Task RenameFileAsync(string oldPath, string newPath);
        Task RenameDirectoryAsync(string oldDirPath, string newDirPath);
        Task UnzipAsync(string zipPath, string targetPath);
        Task ArchiveAsync(List<string> sourcePaths, string targetPath, string format);
        public string ToAbsolutePath(string shareRelativePath);
        public string getRootPath();
        /// <summary>
        /// Calculates the total size of all files within a directory (recursive).
        /// Returns 0 if the directory doesn't exist or is empty.
        /// </summary>
        Task<long> GetDirectorySizeAsync(string directoryPath);
    }
}