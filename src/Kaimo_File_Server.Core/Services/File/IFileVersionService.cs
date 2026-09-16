using Kaimo_File_Server.Core.Domain;

namespace Kaimo_File_Server.Core.Services.File
{
    /// <summary>Outcome of an age-based version retention sweep.</summary>
    public readonly record struct VersionRetentionSweepResult(
        int PathsExamined, int VersionsRemoved, bool MoreWorkPending);

    /// <summary>Outcome of an orphan-blob reclaim pass over a set of storage shards.</summary>
    public readonly record struct OrphanBlobSweepResult(
        int BlobsExamined, int BlobsDeleted, long BytesReclaimed,
        int TempFilesDeleted, int ReadCacheFilesDeleted);

    /// <summary>
    /// Business logic for file versioning.
    /// 
    /// This is the single entry point for all version operations across transports.
    /// It handles:
    ///   - Creating versions on file close (called by SMB, HTTP, NFS)
    ///   - Reading version content (for @GMT- paths in SMB, version endpoints in HTTP)
    ///   - Listing available snapshots (for FSCTL_SRV_ENUMERATE_SNAPSHOTS in SMB, API in HTTP)
    ///   - Retention policy enforcement
    /// 
    /// All methods are async and transport-agnostic.
    /// </summary>
    public interface IFileVersionService
    {
        /// <summary>
        /// Create a new version snapshot of a file.
        /// Call this when a file is closed after being written to.
        /// 
        /// Handles:
        ///   - Content hashing (skips if content unchanged)
        ///   - Blob storage (content-addressable)
        ///   - Version number incrementing
        ///   - Retention policy trimming
        /// 
        /// Returns the created version, or null if content was unchanged.
        ///
        /// <paramref name="shareId"/> scopes the version history: two shares may
        /// each hold a file at the same relative <paramref name="filePath"/>, and
        /// their histories stay isolated.
        /// </summary>
        Task<FileVersion?> CreateVersionAsync(Guid shareId, string filePath, Stream content, string? userId = null);

        /// <summary>
        /// Read the content of a specific version.
        /// Returns a readonly stream.
        /// </summary>
        Task<Stream> ReadVersionAsync(Guid shareId, string filePath, DateTime snapshotTimestampUtc);

        Task<Stream> ReadVersionAsync(
            Guid shareId, string filePath, DateTime snapshotTimestampUtc,
            CancellationToken cancellationToken) =>
            ReadVersionAsync(shareId, filePath, snapshotTimestampUtc)
                .WaitAsync(cancellationToken);

        /// <summary>
        /// Get all versions of a file (newest first).
        /// </summary>
        Task<List<FileVersion>> GetVersionsAsync(Guid shareId, string filePath);

        /// <summary>
        /// Get all distinct snapshot timestamps across all files in a share.
        /// This is what SMB's FSCTL_SRV_ENUMERATE_SNAPSHOTS returns.
        /// Also useful for HTTP API "list all snapshots" endpoint.
        /// </summary>
        Task<List<DateTime>> GetSnapshotTimestampsAsync(Guid shareId, string pathPrefix = "");

        /// <summary>
        /// Point-in-time state of a folder: for every versioned file under
        /// <paramref name="folderPath"/>, the newest version at or before
        /// <paramref name="asOfUtc"/>. Files that did not exist yet at that time
        /// are omitted. Used by the web UI to browse a folder "as of" a snapshot.
        /// </summary>
        Task<List<FileVersion>> GetFolderSnapshotAsync(Guid shareId, string folderPath, DateTime asOfUtc);

        Task<List<FileVersion>> GetFolderSnapshotAsync(
            Guid shareId, string folderPath, DateTime asOfUtc,
            CancellationToken cancellationToken) =>
            GetFolderSnapshotAsync(shareId, folderPath, asOfUtc)
                .WaitAsync(cancellationToken);

        /// <summary>
        /// Get metadata for a file at a specific snapshot time.
        /// Used to resolve @GMT- paths in SMB and version-specific requests in HTTP.
        /// Returns null if no version exists at that timestamp.
        /// </summary>
        Task<FileVersion?> GetVersionAtAsync(Guid shareId, string filePath, DateTime snapshotTimestampUtc);

        /// <summary>
        /// Apply retention policy: delete versions older than maxAge or exceeding maxCount.
        /// Can be called periodically by a background job.
        /// </summary>
        Task<int> ApplyRetentionAsync(Guid shareId, string filePath, int? maxVersions = null, TimeSpan? maxAge = null);

        /// <summary>Moves a file or directory's complete version history to a new path.</summary>
        Task RenamePathAsync(
            Guid shareId,
            string oldPath,
            string newPath,
            Guid? sambaLifecycleEventId = null);

        /// <summary>Deletes all versions at a file/directory path and reclaims unused blobs.</summary>
        Task<int> DeletePathAsync(Guid shareId, string path);

        /// <summary>Deletes all versions for a share and reclaims unused blobs.</summary>
        Task<int> DeleteShareAsync(Guid shareId);

        /// <summary>
        /// Age-based retention sweep across the whole system: for every file with versions
        /// older than <paramref name="maxAge"/>, deletes the expired versions while ALWAYS
        /// keeping the newest <paramref name="minVersionsToKeep"/>. Bounded to
        /// <paramref name="maxPaths"/> files per call; the result reports whether more work
        /// is pending. Never empties a file's history.
        /// </summary>
        Task<VersionRetentionSweepResult> SweepExpiredVersionsAsync(
            TimeSpan maxAge, int minVersionsToKeep, int maxPaths, CancellationToken cancellationToken = default);

        /// <summary>
        /// Reclaims unreferenced version blobs under the given storage shards, plus stale
        /// write temporaries and read-cache files. Only deletes a blob that is unreferenced,
        /// older than <paramref name="minimumAge"/>, and still unreferenced on a final
        /// re-check under the blob lock.
        /// </summary>
        Task<OrphanBlobSweepResult> ReclaimOrphanBlobsAsync(
            IReadOnlyList<string> shardPrefixes, TimeSpan minimumAge, CancellationToken cancellationToken = default);
    }
}
