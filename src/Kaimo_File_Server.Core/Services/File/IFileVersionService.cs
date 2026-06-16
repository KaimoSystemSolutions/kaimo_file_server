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
        /// </summary>
        Task<FileVersion?> CreateVersionAsync(string filePath, Stream content, string? userId = null);

        /// <summary>
        /// Read the content of a specific version.
        /// Returns a readonly stream.
        /// </summary>
        Task<Stream> ReadVersionAsync(string filePath, DateTime snapshotTimestampUtc);

        /// <summary>
        /// Get all versions of a file (newest first).
        /// </summary>
        Task<List<FileVersion>> GetVersionsAsync(string filePath);

        /// <summary>
        /// Get all distinct snapshot timestamps across all files.
        /// This is what SMB's FSCTL_SRV_ENUMERATE_SNAPSHOTS returns.
        /// Also useful for HTTP API "list all snapshots" endpoint.
        /// </summary>
        Task<List<DateTime>> GetSnapshotTimestampsAsync(string pathPrefix = "");

        /// <summary>
        /// Get metadata for a file at a specific snapshot time.
        /// Used to resolve @GMT- paths in SMB and version-specific requests in HTTP.
        /// Returns null if no version exists at that timestamp.
        /// </summary>
        Task<FileVersion?> GetVersionAtAsync(string filePath, DateTime snapshotTimestampUtc);

        /// <summary>
        /// Apply retention policy: delete versions older than maxAge or exceeding maxCount.
        /// Can be called periodically by a background job.
        /// </summary>
        Task<int> ApplyRetentionAsync(string filePath, int? maxVersions = null, TimeSpan? maxAge = null);
    }
}