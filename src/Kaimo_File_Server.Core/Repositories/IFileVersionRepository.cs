using Kaimo_File_Server.Core.Domain;

namespace Kaimo_File_Server.Core.Repositories
{
    /// <summary>
    /// Repository for file version history.
    /// Lives in Core so it is available to all transports (SMB, HTTP, NFS).
    /// The implementation in Infrastructure handles the actual database queries.
    /// </summary>
    public interface IFileVersionRepository
    {
        /// <summary>
        /// Returns all versions of a file, ordered by
        /// <see cref="FileVersion.SnapshotTimestampUtc"/> descending (newest first).
        /// </summary>
        /// <param name="filePath">The normalised relative file path.</param>
        Task<List<FileVersion>> GetVersionsAsync(string filePath);

        /// <summary>
        /// Resolves a specific version by its snapshot timestamp.
        /// Used when handling @GMT- paths from SMB or version requests from HTTP.
        /// </summary>
        /// <param name="filePath">The normalised relative file path.</param>
        /// <param name="snapshotTimestampUtc">The exact UTC timestamp of the snapshot.</param>
        Task<FileVersion?> GetVersionAsync(string filePath, DateTime snapshotTimestampUtc);

        /// <summary>
        /// Returns all distinct snapshot timestamps for a given file.
        /// This is what <c>FSCTL_SRV_ENUMERATE_SNAPSHOTS</c> needs at file level
        /// and what an HTTP "list versions" endpoint would return.
        /// </summary>
        /// <param name="filePath">The normalised relative file path.</param>
        Task<List<DateTime>> GetSnapshotTimestampsAsync(string filePath);

        /// <summary>
        /// Returns all distinct snapshot timestamps across every file
        /// under a given path prefix (or the entire share when empty).
        /// SMB's <c>FSCTL_SRV_ENUMERATE_SNAPSHOTS</c> operates at share level.
        /// </summary>
        /// <param name="pathPrefix">
        /// A path prefix to filter by, or an empty string for the whole share.
        /// </param>
        Task<List<DateTime>> GetAllSnapshotTimestampsAsync(string pathPrefix = "");

        /// <summary>
        /// Persists a new version entry.
        /// </summary>
        /// <param name="version">The version to store.</param>
        /// <returns>The created version with server-generated fields populated.</returns>
        Task<FileVersion> CreateAsync(FileVersion version);

        /// <summary>
        /// Returns the highest version number for a file (used when incrementing).
        /// Returns <c>0</c> if no versions exist yet.
        /// </summary>
        /// <param name="filePath">The normalised relative file path.</param>
        Task<int> GetMaxVersionNumberAsync(string filePath);

        /// <summary>
        /// Deletes all versions with a snapshot timestamp older than <paramref name="cutoff"/>.
        /// Used by retention policies.
        /// </summary>
        /// <param name="filePath">The normalised relative file path.</param>
        /// <param name="cutoff">Versions older than this UTC date are removed.</param>
        /// <returns>The number of deleted version records.</returns>
        Task<int> DeleteOlderThanAsync(string filePath, DateTime cutoff);

        /// <summary>
        /// Trims excess versions beyond <paramref name="maxCount"/>,
        /// keeping the newest ones. Used by retention policies.
        /// </summary>
        /// <param name="filePath">The normalised relative file path.</param>
        /// <param name="maxCount">Maximum number of versions to keep.</param>
        /// <returns>The number of deleted version records.</returns>
        Task<int> TrimToMaxVersionsAsync(string filePath, int maxCount);

        /// <summary>
        /// Checks whether a version with the given content hash already exists.
        /// Enables skipping version creation when the file content has not changed.
        /// </summary>
        /// <param name="filePath">The normalised relative file path.</param>
        /// <param name="contentHash">The SHA-256 (or equivalent) hash of the file content.</param>
        Task<bool> ExistsWithHashAsync(string filePath, string contentHash);
    }
}