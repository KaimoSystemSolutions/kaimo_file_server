using Kaimo_File_Server.Core.Domain;

namespace Kaimo_File_Server.Core.Repositories
{
    /// <summary>
    /// Repository for file version history.
    /// Lives in Core so it's available to all transports (SMB, HTTP, NFS).
    /// 
    /// The implementation in Infrastructure handles the actual DB queries.
    /// </summary>
    public interface IFileVersionRepository
    {
        /// <summary>
        /// Get all versions of a file, ordered by SnapshotTimestampUtc descending (newest first).
        /// </summary>
        Task<List<FileVersion>> GetVersionsAsync(string filePath);

        /// <summary>
        /// Get a specific version by its snapshot timestamp.
        /// Used when resolving @GMT- paths from SMB or version requests from HTTP.
        /// </summary>
        Task<FileVersion?> GetVersionAsync(string filePath, DateTime snapshotTimestampUtc);

        /// <summary>
        /// Get all distinct snapshot timestamps for a given file.
        /// This is what FSCTL_SRV_ENUMERATE_SNAPSHOTS needs for SMB,
        /// and what an HTTP "list versions" endpoint would return.
        /// </summary>
        Task<List<DateTime>> GetSnapshotTimestampsAsync(string filePath);

        /// <summary>
        /// Get all distinct snapshot timestamps across ALL files in a share/path prefix.
        /// SMB's FSCTL_SRV_ENUMERATE_SNAPSHOTS operates at share level, not file level.
        /// </summary>
        Task<List<DateTime>> GetAllSnapshotTimestampsAsync(string pathPrefix = "");

        /// <summary>
        /// Create a new version entry.
        /// </summary>
        Task<FileVersion> CreateAsync(FileVersion version);

        /// <summary>
        /// Get the highest version number for a file (for incrementing).
        /// Returns 0 if no versions exist.
        /// </summary>
        Task<int> GetMaxVersionNumberAsync(string filePath);

        /// <summary>
        /// Delete versions older than the given date for a specific file.
        /// Used by retention policies.
        /// </summary>
        Task<int> DeleteOlderThanAsync(string filePath, DateTime cutoff);

        /// <summary>
        /// Delete excess versions beyond maxCount for a specific file,
        /// keeping the newest ones. Returns number of deleted versions.
        /// </summary>
        Task<int> TrimToMaxVersionsAsync(string filePath, int maxCount);

        /// <summary>
        /// Check if a version with the given content hash already exists for this file.
        /// Enables skipping version creation if content hasn't changed.
        /// </summary>
        Task<bool> ExistsWithHashAsync(string filePath, string contentHash);
    }
}