using Kaimo_File_Server.Core.Domain.Identity;

namespace Kaimo_File_Server.Core.Services.Sync
{
    /// <summary>One item in a sync delta enumeration.</summary>
    /// <param name="Path">Share-relative path (forward slashes, root = "").</param>
    /// <param name="IsDirectory">Whether the item is a directory.</param>
    /// <param name="Size">Content size in bytes (0 for directories).</param>
    /// <param name="ModifiedAtUtc">Last content/metadata modification time (UTC).</param>
    public readonly record struct SyncEntry(
        string Path,
        bool IsDirectory,
        long Size,
        DateTime ModifiedAtUtc);

    /// <summary>Result of enumerating a synced subtree for one device.</summary>
    /// <param name="Entries">
    /// Every file and directory under the requested root that the user may read,
    /// depth-first. The client diffs this against its last-known state to derive
    /// adds/updates/deletes and drive transfers according to the sync direction.
    /// </param>
    /// <param name="Token">
    /// The subtree's <see cref="ShareChangeState"/> token captured for this
    /// enumeration; pass it to the change-wait endpoint to be notified of the
    /// next change.
    /// </param>
    /// <param name="Seq">
    /// The change-log head sequence for the subtree captured before the walk. A client
    /// establishes its baseline from this full enumeration, then switches to the incremental
    /// <c>changes?since=Seq</c> feed for subsequent catch-up.
    /// </param>
    public sealed record SyncDelta(IReadOnlyList<SyncEntry> Entries, string Token, long Seq);

    /// <summary>
    /// Thin, ACL-checked sync primitive: enumerates a synced subtree by composing
    /// existing <see cref="File.IFileService"/> listings. The server holds no
    /// per-device sync state and performs no conflict resolution — that logic
    /// lives in the client. Every listing is permission-filtered, so a user only
    /// ever sees what their ACLs allow.
    /// </summary>
    public interface ISyncQueryService
    {
        /// <summary>
        /// Enumerates every readable item under <paramref name="rootRelativePath"/>
        /// in the given share for <paramref name="user"/>.
        /// </summary>
        /// <param name="shareId">The share to enumerate.</param>
        /// <param name="rootRelativePath">Normalized share-relative subtree root (empty = whole share).</param>
        /// <param name="user">The caller, whose ACLs bound the result.</param>
        Task<SyncDelta> EnumerateAsync(
            Guid shareId,
            string rootRelativePath,
            UserContext user,
            CancellationToken ct = default);
    }
}
