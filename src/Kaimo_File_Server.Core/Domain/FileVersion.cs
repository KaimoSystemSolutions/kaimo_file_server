namespace Kaimo_File_Server.Core.Domain
{
    /// <summary>
    /// Represents a single historical version of a file.
    /// Each write-and-close cycle creates a new version.
    /// 
    /// The SnapshotTimestamp is stored as UTC and formatted as @GMT-YYYY.MM.DD-HH.MM.SS
    /// for Windows "Previous Versions" compatibility via SMB.
    /// 
    /// This entity is transport-agnostic usable from SMB, HTTP, NFS, or any future adapter.
    /// </summary>
    public class FileVersion
    {
        public Guid Id { get; set; }

        /// <summary>
        /// The storage-relative path of the file (e.g. "docs/report.docx").
        /// Not a FK to FileMetadata versions can exist even if metadata hasn't been created yet.
        /// </summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>
        /// UTC timestamp of when this version was created (= snapshot time).
        /// Used to build the @GMT- string for SMB and as a version identifier for HTTP APIs.
        /// </summary>
        public DateTime SnapshotTimestampUtc { get; set; }

        /// <summary>
        /// Where the version's content is stored on disk, relative to the version storage root.
        /// E.g. "versions/ab/cd/abcdef12-3456-7890-abcd-ef1234567890.bin"
        /// </summary>
        public string StoragePath { get; set; } = string.Empty;

        /// <summary>
        /// SHA-256 hash of the file content. Enables deduplication:
        /// if two versions have the same hash, they share the same blob.
        /// </summary>
        public string ContentHash { get; set; } = string.Empty;

        /// <summary>
        /// File size in bytes at the time of this version.
        /// </summary>
        public long Size { get; set; }

        /// <summary>
        /// Who created this version (user ID as string for flexibility).
        /// </summary>
        public string? CreatedBy { get; set; }

        /// <summary>
        /// Sequential version number per file (1, 2, 3, ...).
        /// Makes it easy to show "Version 3 of 7" in any UI.
        /// </summary>
        public int VersionNumber { get; set; }

        internal FileVersion() { }

        public FileVersion(string filePath, DateTime snapshotTimestampUtc,
            string storagePath, string contentHash, long size,
            string? createdBy, int versionNumber)
        {
            Id = Guid.NewGuid();
            FilePath = filePath;
            SnapshotTimestampUtc = snapshotTimestampUtc;
            StoragePath = storagePath;
            ContentHash = contentHash;
            Size = size;
            CreatedBy = createdBy;
            VersionNumber = versionNumber;
        }

        /// <summary>
        /// Returns the @GMT- formatted string that Windows expects.
        /// Format: @GMT-YYYY.MM.DD-HH.MM.SS
        /// </summary>
        public string ToGmtToken()
        {
            return SnapshotTimestampUtc.ToString("'@GMT-'yyyy.MM.dd-HH.mm.ss");
        }

        /// <summary>
        /// Parses a @GMT- token back to a UTC DateTime.
        /// Returns null if the format is invalid.
        /// </summary>
        public static DateTime? ParseGmtToken(string token)
        {
            if (string.IsNullOrEmpty(token) || !token.StartsWith("@GMT-"))
                return null;

            if (DateTime.TryParseExact(token, "'@GMT-'yyyy.MM.dd-HH.mm.ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var result))
            {
                return result;
            }

            return null;
        }
    }
}