using Kaimo_File_Server.Core.Domain;

namespace Kaimo_File_Server.Core.Services.File
{
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
        Task RenamePathAsync(Guid shareId, string oldPath, string newPath);

        /// <summary>Deletes all versions at a file/directory path and reclaims unused blobs.</summary>
        Task<int> DeletePathAsync(Guid shareId, string path);

        /// <summary>Deletes all versions for a share and reclaims unused blobs.</summary>
        Task<int> DeleteShareAsync(Guid shareId);
    }
}
