using System;
using System.Globalization;

namespace Kaimo_File_Server.Core.Domain
{
    /// <summary>
    /// Represents a single historical version of a file.
    /// A new version is created on every write-and-close cycle.
    ///
    /// The <see cref="SnapshotTimestampUtc"/> is formatted as
    /// <c>@GMT-YYYY.MM.DD-HH.MM.SS</c> for compatibility with
    /// Windows "Previous Versions" via SMB.
    ///
    /// This entity is transport-agnostic and can be consumed by
    /// SMB, HTTP, NFS, or any future protocol adapter.
    /// </summary>
    public class FileVersion
    {
        /// <summary>Unique identifier for this version record.</summary>
        public Guid Id { get; set; }

        /// <summary>
        /// The share this version belongs to. Version history is scoped per
        /// share: two different shares may each contain a file at the same
        /// share-relative <see cref="FilePath"/> (e.g. "report.docx" at the
        /// root), and their histories must stay isolated. Every version query
        /// filters by this id.
        ///
        /// Legacy rows created before per-share scoping carry
        /// <see cref="Guid.Empty"/> and therefore never match a real share —
        /// they are treated as inaccessible rather than leaked cross-share.
        /// </summary>
        public Guid ShareId { get; set; }

        /// <summary>
        /// Storage-relative path of the file (e.g. "docs/report.docx").
        /// Not a foreign key to <see cref="FileMetadata"/> — versions may
        /// exist before metadata has been created.
        /// </summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>
        /// UTC timestamp of when this version was captured.
        /// Used to build the <c>@GMT-</c> token for SMB and as the
        /// version identifier for HTTP APIs.
        /// </summary>
        public DateTime SnapshotTimestampUtc { get; set; }

        /// <summary>
        /// On-disk location of the version blob, relative to the
        /// version storage root (e.g.
        /// "versions/ab/cd/abcdef12-…-567890.bin").
        /// </summary>
        public string StoragePath { get; set; } = string.Empty;

        /// <summary>
        /// SHA-256 hash of the file content.
        /// Enables content-addressable deduplication: versions with
        /// identical hashes share the same physical blob.
        /// </summary>
        public string ContentHash { get; set; } = string.Empty;

        /// <summary>File size in bytes at the time of this version.</summary>
        public long Size { get; set; }

        /// <summary>
        /// Id (as string) of the user who triggered this version.
        /// Stored as a string for flexibility with external identity providers.
        /// </summary>
        public string? CreatedBy { get; set; }

        /// <summary>
        /// Sequential per-file version number (1, 2, 3, …).
        /// Makes it easy to display "Version 3 of 7" in any UI.
        /// </summary>
        public int VersionNumber { get; set; }

        private const string GmtFormat = "'@GMT-'yyyy.MM.dd-HH.mm.ss";

        /// <summary>EF Core / serialization constructor.</summary>
        internal FileVersion() { }

        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="filePath"/>, <paramref name="storagePath"/>,
        /// or <paramref name="contentHash"/> is null or whitespace.
        /// </exception>
        public FileVersion(
            Guid shareId,
            string filePath,
            DateTime snapshotTimestampUtc,
            string storagePath,
            string contentHash,
            long size,
            string? createdBy,
            int versionNumber)
        {
            Id = Guid.NewGuid();

            ShareId = shareId;

            FilePath = !string.IsNullOrWhiteSpace(filePath)
                ? filePath
                : throw new ArgumentException("Must not be empty.", nameof(filePath));

            SnapshotTimestampUtc = snapshotTimestampUtc;

            StoragePath = !string.IsNullOrWhiteSpace(storagePath)
                ? storagePath
                : throw new ArgumentException("Must not be empty.", nameof(storagePath));

            ContentHash = !string.IsNullOrWhiteSpace(contentHash)
                ? contentHash
                : throw new ArgumentException("Must not be empty.", nameof(contentHash));

            Size = size;
            CreatedBy = createdBy;
            VersionNumber = versionNumber;
        }

        /// <summary>
        /// Returns the <c>@GMT-</c> formatted string that Windows
        /// "Previous Versions" expects (e.g. <c>@GMT-2025.05.28-14.30.00</c>).
        /// </summary>
        public string ToGmtToken()
            => SnapshotTimestampUtc.ToString(GmtFormat, CultureInfo.InvariantCulture);

        /// <summary>
        /// Parses a <c>@GMT-</c> token back to a UTC <see cref="DateTime"/>.
        /// Returns <c>null</c> if the format is invalid.
        /// </summary>
        public static DateTime? ParseGmtToken(string token)
        {
            if (string.IsNullOrEmpty(token) || !token.StartsWith("@GMT-", StringComparison.Ordinal))
                return null;

            return DateTime.TryParseExact(
                token,
                GmtFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var result)
                ? result
                : null;
        }
    }
}