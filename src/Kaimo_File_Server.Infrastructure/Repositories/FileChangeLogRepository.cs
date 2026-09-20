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

    public async Task AppendAsync(FileChangeLogEntry entry, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.FileChangeLog.Add(entry);
        await db.SaveChangesAsync(ct);
    }

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
