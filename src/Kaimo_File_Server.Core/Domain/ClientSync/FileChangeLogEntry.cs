using System;

namespace Kaimo_File_Server.Core.Domain.ClientSync
{
    /// <summary>
    /// The kind of mutation a <see cref="FileChangeLogEntry"/> records.
    /// Serialized by name over the wire (e.g. <c>"Renamed"</c>).
    /// </summary>
    public enum FileChangeType
    {
        /// <summary>A file or directory was created.</summary>
        Created,

        /// <summary>An existing file's content was overwritten.</summary>
        Modified,

        /// <summary>A file or directory was deleted (or moved to the recycle bin — see <see cref="Renamed"/>).</summary>
        Deleted,

        /// <summary>
        /// A file or directory was renamed or moved. <see cref="FileChangeLogEntry.OldPath"/>
        /// is the source and <see cref="FileChangeLogEntry.Path"/> the destination. A move to the
        /// recycle bin is recorded this way; a client watching the source subtree reads it as a
        /// disappearance (the destination lies outside the subtree).
        /// </summary>
        Renamed,

        /// <summary>
        /// A bulk operation (archive/unzip/folder restore) changed an unbounded number of items
        /// under <see cref="FileChangeLogEntry.Path"/>. The client reconciles that subtree with a
        /// scoped <c>delta</c> rather than expecting a per-file entry.
        /// </summary>
        SubtreeChanged,
    }

    /// <summary>
    /// One append-only entry in the per-share change log. <see cref="Seq"/> is a globally
    /// monotonic identity value; a client stores the highest <see cref="Seq"/> it has seen for a
    /// share and asks for everything with a greater <see cref="Seq"/> under the subtree it watches
    /// (<c>GET /api/v1/sync/{shareId}/changes?since=N</c>). Unlike the coarse
    /// <c>newestModified:itemCount</c> fingerprint, this captures renames and net-zero changes,
    /// because every mutation appends its own row regardless of how it moves the aggregate.
    ///
    /// The log is written best-effort from the <see cref="Kaimo_File_Server.Core.Services.File.IFileService"/>
    /// mutation paths (all transports funnel through them). A rare lost row is recoverable by the
    /// client's periodic full <c>delta</c> reconcile, so it is intentionally not part of the file
    /// operation's transaction.
    /// </summary>
    public sealed class FileChangeLogEntry
    {
        /// <summary>Globally monotonic identity primary key. The client's opaque cursor.</summary>
        public long Seq { get; set; }

        /// <summary>The share the changed item belongs to.</summary>
        public Guid ShareId { get; set; }

        /// <summary>
        /// Share-relative path (forward slashes) of the affected item. For a
        /// <see cref="FileChangeType.Renamed"/> entry this is the destination path; for
        /// <see cref="FileChangeType.SubtreeChanged"/> it is the affected root.
        /// </summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>
        /// The source path of a <see cref="FileChangeType.Renamed"/> entry; <c>null</c> otherwise.
        /// </summary>
        public string? OldPath { get; set; }

        /// <summary>What happened to the item.</summary>
        public FileChangeType ChangeType { get; set; }

        /// <summary><c>true</c> if the item is a directory.</summary>
        public bool IsDirectory { get; set; }

        /// <summary>
        /// Size in bytes at the time of the change, when cheaply available; <c>null</c> for
        /// deletes, directories, and bulk entries. Lets a client rebuild the item tag
        /// (<c>"{size}:{modifiedTicks}"</c>) without a metadata round trip.
        /// </summary>
        public long? Size { get; set; }

        /// <summary>
        /// The item's modified time (UTC) at the time of the change, when available; <c>null</c>
        /// for deletes and bulk entries.
        /// </summary>
        public DateTime? ModifiedAtUtc { get; set; }

        /// <summary>UTC time the entry was appended (drives retention pruning).</summary>
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
