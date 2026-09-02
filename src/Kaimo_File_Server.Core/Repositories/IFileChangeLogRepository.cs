using Kaimo_File_Server.Core.Domain.ClientSync;

namespace Kaimo_File_Server.Core.Repositories
{
    /// <summary>
    /// Append-only persistence for the per-share change log that backs the client API's
    /// <c>change_seq</c> delta feed. Reads are scan-by-cursor (<c>Seq &gt; N</c>), optionally
    /// scoped to a subtree prefix.
    /// </summary>
    public interface IFileChangeLogRepository
    {
        /// <summary>
        /// Appends one entry. The database assigns <see cref="FileChangeLogEntry.Seq"/>; on return
        /// the passed instance carries the assigned value.
        /// </summary>
        Task AppendAsync(FileChangeLogEntry entry, CancellationToken ct = default);

        /// <summary>
        /// Returns up to <paramref name="maxCount"/> entries for the share with
        /// <c>Seq &gt; <paramref name="sinceSeq"/></c>, ordered ascending by <c>Seq</c>. When
        /// <paramref name="pathPrefix"/> is non-empty, an entry is included when either its
        /// <see cref="FileChangeLogEntry.Path"/> or its <see cref="FileChangeLogEntry.OldPath"/>
        /// lies at or under the prefix, so a rename that leaves the subtree still surfaces.
        /// </summary>
        Task<IReadOnlyList<FileChangeLogEntry>> GetChangesSinceAsync(
            Guid shareId, long sinceSeq, string? pathPrefix, int maxCount, CancellationToken ct = default);

        /// <summary>
        /// The highest <c>Seq</c> currently recorded for the share subtree, or <c>0</c> when the
        /// subtree has no logged changes yet. Backs the long-poll change wait.
        /// </summary>
        Task<long> GetHeadSeqAsync(Guid shareId, string? pathPrefix, CancellationToken ct = default);

        /// <summary>Deletes entries appended before <paramref name="cutoffUtc"/>; returns the count removed.</summary>
        Task<int> PruneOlderThanAsync(DateTime cutoffUtc, CancellationToken ct = default);

        /// <summary>
        /// The lowest <c>Seq</c> still retained across the whole log, or <c>0</c> when the log is
        /// empty. Because pruning removes a low-<c>Seq</c> prefix, a client whose cursor precedes this
        /// value has a gap and must re-bootstrap via a full <c>delta</c>.
        /// </summary>
        Task<long> GetOldestSeqAsync(CancellationToken ct = default);
    }
}
