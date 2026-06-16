using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;

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
        
        /// <summary>
        /// Returns the subset of paths the user has ListReadData permission on.
        /// Single DB round trip via batch ACL evaluation.
        ///
        /// Used by SMB QueryDirectory to filter listings without N+1 queries.
        /// </summary>
        Task<HashSet<string>> FilterReadablePathsAsync(
            IReadOnlyList<(string relativePath, bool isDirectory)> items,
            UserContext user);
    }
}