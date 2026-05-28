using System;
using System.Collections.Generic;
using Kaimo_File_Server.Core.Security;

namespace Kaimo_File_Server.Core.Domain
{
    /// <summary>
    /// Cached metadata for a file or directory within a share.
    ///
    /// Stores ownership, timestamps, and a per-item access control list
    /// (<see cref="Acl"/>) that is evaluated by the authorization layer
    /// on every file operation.
    ///
    /// <see cref="Path"/> is relative to the share root and uses
    /// forward-slash separators regardless of the host OS.
    /// </summary>
    public class FileMetadata
    {
        /// <summary>Unique identifier for this metadata record.</summary>
        public Guid Id { get; set; }

        /// <summary>
        /// Foreign key to the <see cref="ShareDefinition"/> this item belongs to.
        /// </summary>
        public Guid ShareId { get; set; }

        /// <summary>
        /// Share-relative path using forward slashes (e.g. "docs/reports").
        /// For the share root itself this is an empty string.
        /// </summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>File or directory name (e.g. "report.docx").</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Size in bytes. Always 0 for directories.</summary>
        public long Size { get; set; }

        /// <summary>
        /// <c>true</c> if this entry represents a directory;
        /// <c>false</c> for a regular file.
        /// </summary>
        public bool IsDirectory { get; set; }

        /// <summary>UTC timestamp when the item was first created.</summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>UTC timestamp of the last content or metadata modification.</summary>
        public DateTime ModifiedAt { get; set; }

        /// <summary>
        /// UTC timestamp of the last read access.
        /// <c>null</c> if the item has never been accessed or tracking is disabled.
        /// </summary>
        public DateTime? LastAccessedAt { get; set; }

        /// <summary>
        /// Id of the <see cref="Identity.User"/> who created or currently owns the item.
        /// Used as the implicit "owner" principal in ACL evaluation.
        /// </summary>
        public Guid OwnerId { get; set; }

        /// <summary>
        /// Ordered access control list evaluated top-to-bottom.
        /// Deny entries take precedence over allow entries at the same level.
        /// </summary>
        public List<AccessEntry> Acl { get; set; } = new();
    }
}