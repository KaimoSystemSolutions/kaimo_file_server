using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IFileChangeLogRepository"/> over the append-only
/// <c>file_change_log</c> table. The <c>(ShareId, Seq)</c> index serves both the cursor scan and
/// the head-sequence lookup.
/// </summary>
public sealed class FileChangeLogRepository : IFileChangeLogRepository
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;

    public FileChangeLogRepository(IDbContextFactory<ApplicationDbContext> dbFactory)
        => _dbFactory = dbFactory;

    /// <summary>
    /// Appends one entry so that commit order equals <c>Seq</c> order. Several processes
    /// (Web, SMB bridge, Host) append concurrently; with a bare IDENTITY insert a lower
    /// <c>Seq</c> could commit after a higher one that a cursor reader (search indexer,
    /// client <c>changes?since=</c>) had already passed, and that entry would be skipped
    /// forever. A transaction-scoped advisory lock serializes the short insert, so a reader
    /// never sees a <c>Seq</c> while a lower one is still uncommitted.
    /// If a COMMIT succeeds but its acknowledgement is lost, the retry appends the entry a
    /// second time under a new <c>Seq</c>. That is accepted on purpose: create/modify/delete
    /// entries are path-based and re-apply harmlessly; only a duplicated rename that lands
    /// after a later opposite rename can leave a stale search hit until the next reindex.
    /// ponytail: global append lock serializes inserts (~1 ms each); switch to xid8
    /// snapshot-horizon reads if append throughput ever matters.
    /// </summary>
    public Task AppendAsync(FileChangeLogEntry entry, CancellationToken ct = default)
        => _dbFactory.ExecuteResilientAsync(async db =>
        {
            // A retried attempt must draw a fresh identity value, never reuse one that a
            // failed attempt may already have written back into the entity.
            entry.Seq = 0;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (db.Database.IsNpgsql())
                await db.Database.ExecuteSqlRawAsync(
                    "SELECT pg_advisory_xact_lock(hashtext('kaimo_file_change_log'))", ct);
            db.FileChangeLog.Add(entry);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }, ct);

    public async Task<IReadOnlyList<FileChangeLogEntry>> GetChangesSinceAsync(
        Guid shareId, long sinceSeq, string? pathPrefix, int maxCount, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var query = db.FileChangeLog.AsNoTracking()
            .Where(e => e.ShareId == shareId && e.Seq > sinceSeq);

        query = ApplyPrefixFilter(query, pathPrefix);

        return await query
            .OrderBy(e => e.Seq)
            .Take(maxCount)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<FileChangeLogEntry>> GetChangesSinceGlobalAsync(
        long sinceSeq, int maxCount, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        return await db.FileChangeLog.AsNoTracking()
            .Where(e => e.Seq > sinceSeq)
            .OrderBy(e => e.Seq)
            .Take(maxCount)
            .ToListAsync(ct);
    }

    public async Task<long> GetHeadSeqAsync(Guid shareId, string? pathPrefix, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var query = db.FileChangeLog.AsNoTracking().Where(e => e.ShareId == shareId);
        query = ApplyPrefixFilter(query, pathPrefix);

        // MaxAsync over an empty set throws; the nullable projection returns null instead.
        return await query.MaxAsync(e => (long?)e.Seq, ct) ?? 0L;
    }

    public async Task<long> GetHeadSeqGlobalAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        // MaxAsync over an empty set throws; the nullable projection returns null instead.
        return await db.FileChangeLog.AsNoTracking().MaxAsync(e => (long?)e.Seq, ct) ?? 0L;
    }

    public async Task<int> PruneOlderThanAsync(DateTime cutoffUtc, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.FileChangeLog
            .Where(e => e.CreatedAtUtc < cutoffUtc)
            .ExecuteDeleteAsync(ct);
    }

    public async Task<long> GetOldestSeqAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        // MinAsync over an empty set throws; the nullable projection returns null instead.
        return await db.FileChangeLog.AsNoTracking().MinAsync(e => (long?)e.Seq, ct) ?? 0L;
    }

    /// <summary>
    /// Restricts a change-log query to a subtree: the root itself plus everything strictly beneath
    /// it, matched on either <c>Path</c> or <c>OldPath</c> so a rename leaving the subtree is still
    /// reported. The <c>"/"</c> guard prevents a sibling like <c>docs2</c> matching prefix
    /// <c>docs</c>. An empty prefix covers the whole share.
    /// </summary>
    private static IQueryable<FileChangeLogEntry> ApplyPrefixFilter(
        IQueryable<FileChangeLogEntry> query, string? pathPrefix)
    {
        if (string.IsNullOrEmpty(pathPrefix))
            return query;

        string childPrefix = pathPrefix + "/";
        return query.Where(e =>
            e.Path == pathPrefix || e.Path.StartsWith(childPrefix) ||
            (e.OldPath != null && (e.OldPath == pathPrefix || e.OldPath.StartsWith(childPrefix))));
    }
}
