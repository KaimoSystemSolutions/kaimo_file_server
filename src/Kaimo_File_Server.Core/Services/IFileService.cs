using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;

namespace Kaimo_File_Server.Core.Services
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
        Task<Stream> ReadFileAsync(string path, UserContext user);
        Task WriteFileAsync(string path, Stream data, UserContext user);
        Task CreateFileAsync(string path, UserContext user);
        Task CreateDirectoryAsync(string path, UserContext user);
        Task DeleteFileAsync(string path, UserContext user, bool isRecycleEnabled);
        Task<FileMetadata> GetMetadataAsync(string path, UserContext user);
        Task<List<FileMetadata>> ListAsync(string directoryPath, UserContext user);
        Task RenameAsync(string oldPath, string newPath, UserContext user);
        Task<long> GetDirectorySizeAsync(string relativePath, UserContext userContext);
    }
}