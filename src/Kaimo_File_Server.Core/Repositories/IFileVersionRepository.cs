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
        /// <param name="shareId">The share the file belongs to.</param>
        /// <param name="filePath">The normalised relative file path.</param>
        Task<List<FileVersion>> GetVersionsAsync(Guid shareId, string filePath);

        /// <summary>
        /// Resolves a specific version by its snapshot timestamp.
        /// Used when handling @GMT- paths from SMB or version requests from HTTP.
        /// </summary>
        /// <param name="shareId">The share the file belongs to.</param>
        /// <param name="filePath">The normalised relative file path.</param>
        /// <param name="snapshotTimestampUtc">The exact UTC timestamp of the snapshot.</param>
        Task<FileVersion?> GetVersionAsync(Guid shareId, string filePath, DateTime snapshotTimestampUtc);

        /// <summary>
        /// Returns all distinct snapshot timestamps for a given file.
        /// This is what <c>FSCTL_SRV_ENUMERATE_SNAPSHOTS</c> needs at file level
        /// and what an HTTP "list versions" endpoint would return.
        /// </summary>
        /// <param name="shareId">The share the file belongs to.</param>
        /// <param name="filePath">The normalised relative file path.</param>
        Task<List<DateTime>> GetSnapshotTimestampsAsync(Guid shareId, string filePath);

        /// <summary>
        /// Returns all distinct snapshot timestamps across every file
        /// under a given path prefix (or the entire share when empty).
        /// SMB's <c>FSCTL_SRV_ENUMERATE_SNAPSHOTS</c> operates at share level.
        /// </summary>
        /// <param name="shareId">The share to enumerate.</param>
        /// <param name="pathPrefix">
        /// A path prefix to filter by, or an empty string for the whole share.
        /// </param>
        Task<List<DateTime>> GetAllSnapshotTimestampsAsync(Guid shareId, string pathPrefix = "");

        /// <summary>
        /// Returns, for every distinct file under <paramref name="pathPrefix"/>,
        /// the newest version whose snapshot timestamp is at or before
        /// <paramref name="asOfUtc"/>. This is the "point-in-time" state of a
        /// folder: what each file looked like at that moment. Files that did not
        /// yet exist at that time are omitted.
        /// </summary>
        /// <param name="shareId">The share to enumerate.</param>
        /// <param name="pathPrefix">
        /// A path prefix to filter by, or an empty string for the whole share.
        /// </param>
        /// <param name="asOfUtc">The upper bound (inclusive) for the snapshot timestamp.</param>
        Task<List<FileVersion>> GetLatestVersionsUnderPrefixAsync(Guid shareId, string pathPrefix, DateTime asOfUtc);

        Task<List<FileVersion>> GetLatestVersionsUnderPrefixAsync(
            Guid shareId, string pathPrefix, DateTime asOfUtc,
            CancellationToken cancellationToken) =>
            GetLatestVersionsUnderPrefixAsync(shareId, pathPrefix, asOfUtc)
                .WaitAsync(cancellationToken);

        /// <summary>
        /// Persists a new version entry.
        /// </summary>
        /// <param name="version">The version to store (already carries its <see cref="FileVersion.ShareId"/>).</param>
        /// <returns>The created version with server-generated fields populated.</returns>
        Task<FileVersion> CreateAsync(FileVersion version);

        /// <summary>
        /// Returns the highest version number for a file (used when incrementing).
        /// Returns <c>0</c> if no versions exist yet.
        /// </summary>
        /// <param name="shareId">The share the file belongs to.</param>
        /// <param name="filePath">The normalised relative file path.</param>
        Task<int> GetMaxVersionNumberAsync(Guid shareId, string filePath);

        /// <summary>
        /// Deletes all versions with a snapshot timestamp older than <paramref name="cutoff"/>.
        /// Used by retention policies.
        /// </summary>
        /// <param name="shareId">The share the file belongs to.</param>
        /// <param name="filePath">The normalised relative file path.</param>
        /// <param name="cutoff">Versions older than this UTC date are removed.</param>
        /// <returns>The deleted rows, used for physical blob garbage collection.</returns>
        Task<List<FileVersion>> DeleteOlderThanAsync(Guid shareId, string filePath, DateTime cutoff);

        /// <summary>
        /// Trims excess versions beyond <paramref name="maxCount"/>,
        /// keeping the newest ones. Used by retention policies.
        /// </summary>
        /// <param name="shareId">The share the file belongs to.</param>
        /// <param name="filePath">The normalised relative file path.</param>
        /// <param name="maxCount">Maximum number of versions to keep.</param>
        /// <returns>The deleted rows, used for physical blob garbage collection.</returns>
        Task<List<FileVersion>> TrimToMaxVersionsAsync(Guid shareId, string filePath, int maxCount);

        /// <summary>
        /// Deletes every version at <paramref name="path"/> or below it and returns
        /// the removed rows so their content-addressed blobs can be garbage-collected.
        /// </summary>
        Task<List<FileVersion>> DeletePathAsync(Guid shareId, string path);

        /// <summary>
        /// Renames a file or directory version-history prefix. Existing destination
        /// history is removed and returned because a replace-style filesystem rename
        /// has displaced that object.
        /// </summary>
        Task<List<FileVersion>> RenamePathAsync(
            Guid shareId, string oldPath, string newPath);

        /// <summary>Deletes all version rows belonging to a share.</summary>
        Task<List<FileVersion>> DeleteShareAsync(Guid shareId);

        /// <summary>Whether any remaining version row references a physical blob.</summary>
        Task<bool> IsStoragePathReferencedAsync(string storagePath);

        /// <summary>
        /// Checks whether a version with the given content hash already exists.
        /// Enables skipping version creation when the file content has not changed.
        /// </summary>
        /// <param name="shareId">The share the file belongs to.</param>
        /// <param name="filePath">The normalised relative file path.</param>
        /// <param name="contentHash">The SHA-256 (or equivalent) hash of the file content.</param>
        Task<bool> ExistsWithHashAsync(Guid shareId, string filePath, string contentHash);
    }
}
