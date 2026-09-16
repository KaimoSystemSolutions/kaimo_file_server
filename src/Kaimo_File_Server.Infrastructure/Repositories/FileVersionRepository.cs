using Microsoft.EntityFrameworkCore;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Persistence;
using System.Data;

namespace Kaimo_File_Server.Infrastructure.Repositories
{
    public class FileVersionRepository : IFileVersionRepository
    {
        private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;

        public FileVersionRepository(IDbContextFactory<ApplicationDbContext> db)
        {
            _dbFactory = db;
        }

        public async Task<List<FileVersion>> GetVersionsAsync(Guid shareId, string filePath)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            return await db.Set<FileVersion>()
                .Where(v => v.ShareId == shareId && v.FilePath == filePath)
                .OrderByDescending(v => v.SnapshotTimestampUtc)
                .ToListAsync();
        }

        public async Task<FileVersion?> GetVersionAsync(Guid shareId, string filePath, DateTime snapshotTimestampUtc)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            return await db.Set<FileVersion>()
                .FirstOrDefaultAsync(v =>
                    v.ShareId == shareId &&
                    v.FilePath == filePath &&
                    v.SnapshotTimestampUtc == snapshotTimestampUtc);
        }

        public async Task<List<DateTime>> GetSnapshotTimestampsAsync(Guid shareId, string filePath)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            return await db.Set<FileVersion>()
                .Where(v => v.ShareId == shareId && v.FilePath == filePath)
                .Select(v => v.SnapshotTimestampUtc)
                .Distinct()
                .OrderByDescending(t => t)
                .ToListAsync();
        }

        public async Task<List<DateTime>> GetAllSnapshotTimestampsAsync(Guid shareId, string pathPrefix = "")
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var query = db.Set<FileVersion>()
                .Where(v => v.ShareId == shareId);

            if (!string.IsNullOrEmpty(pathPrefix))
                query = query.Where(v => v.FilePath.StartsWith(pathPrefix));

            return await query
                .Select(v => v.SnapshotTimestampUtc)
                .Distinct()
                .OrderByDescending(t => t)
                .ToListAsync();
        }

        public async Task<List<FileVersion>> GetLatestVersionsUnderPrefixAsync(
            Guid shareId, string pathPrefix, DateTime asOfUtc)
            => await GetLatestVersionsUnderPrefixAsync(
                shareId, pathPrefix, asOfUtc, CancellationToken.None);

        public async Task<List<FileVersion>> GetLatestVersionsUnderPrefixAsync(
            Guid shareId, string pathPrefix, DateTime asOfUtc,
            CancellationToken cancellationToken)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var query = db.Set<FileVersion>()
                .Where(v => v.ShareId == shareId && v.SnapshotTimestampUtc <= asOfUtc);

            if (!string.IsNullOrEmpty(pathPrefix))
                query = query.Where(v => v.FilePath.StartsWith(pathPrefix));

            // Retention caps versions per file, so the candidate set stays small.
            // Group in memory to pick the newest snapshot at-or-before the cutoff.
            var candidates = await query.ToListAsync(cancellationToken);

            return candidates
                .GroupBy(v => v.FilePath)
                .Select(g => g.OrderByDescending(v => v.SnapshotTimestampUtc).First())
                .OrderBy(v => v.FilePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public async Task<FileVersion> CreateAsync(FileVersion version)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            db.Set<FileVersion>().Add(version);
            await db.SaveChangesAsync();
            return version;
        }

        public async Task<int> GetMaxVersionNumberAsync(Guid shareId, string filePath)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var max = await db.Set<FileVersion>()
                .Where(v => v.ShareId == shareId && v.FilePath == filePath)
                .MaxAsync(v => (int?)v.VersionNumber);
            return max ?? 0;
        }

        public async Task<List<FileVersion>> DeleteOlderThanAsync(Guid shareId, string filePath, DateTime cutoff)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var toDelete = await db.Set<FileVersion>()
                .Where(v => v.ShareId == shareId && v.FilePath == filePath && v.SnapshotTimestampUtc < cutoff)
                .ToListAsync();

            db.Set<FileVersion>().RemoveRange(toDelete);
            await db.SaveChangesAsync();
            return toDelete;
        }

        public async Task<List<FileVersion>> TrimToMaxVersionsAsync(Guid shareId, string filePath, int maxCount)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            // Get IDs of versions to keep (newest N)
            var keepIds = await db.Set<FileVersion>()
                .Where(v => v.ShareId == shareId && v.FilePath == filePath)
                .OrderByDescending(v => v.SnapshotTimestampUtc)
                .Take(maxCount)
                .Select(v => v.Id)
                .ToListAsync();

            var toDelete = await db.Set<FileVersion>()
                .Where(v => v.ShareId == shareId && v.FilePath == filePath && !keepIds.Contains(v.Id))
                .ToListAsync();

            if (toDelete.Count == 0) return [];

            db.Set<FileVersion>().RemoveRange(toDelete);
            await db.SaveChangesAsync();
            return toDelete;
        }

        public async Task<List<FileVersion>> DeleteOlderThanAsync(
            Guid shareId, string filePath, DateTime cutoff, int keepNewest)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            // Exclude the newest `keepNewest` ids from the delete set (same technique as
            // TrimToMaxVersionsAsync). This is the retention FLOOR: age-based deletion must
            // never empty a file's history — see FileVersionService.SweepExpiredVersionsAsync.
            var keepIds = await db.Set<FileVersion>()
                .Where(v => v.ShareId == shareId && v.FilePath == filePath)
                .OrderByDescending(v => v.SnapshotTimestampUtc)
                .Take(keepNewest)
                .Select(v => v.Id)
                .ToListAsync();

            var toDelete = await db.Set<FileVersion>()
                .Where(v => v.ShareId == shareId && v.FilePath == filePath
                            && v.SnapshotTimestampUtc < cutoff && !keepIds.Contains(v.Id))
                .ToListAsync();

            if (toDelete.Count == 0) return [];

            db.Set<FileVersion>().RemoveRange(toDelete);
            await db.SaveChangesAsync();
            return toDelete;
        }

        public async Task<IReadOnlyList<(Guid ShareId, string FilePath)>> GetPathsWithVersionsOlderThanAsync(
            DateTime cutoff, int limit, CancellationToken cancellationToken = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var rows = await db.Set<FileVersion>()
                .Where(v => v.SnapshotTimestampUtc < cutoff)
                .Select(v => new { v.ShareId, v.FilePath })
                .Distinct()
                .OrderBy(x => x.ShareId).ThenBy(x => x.FilePath)
                .Take(limit)
                .ToListAsync(cancellationToken);

            return rows.Select(x => (x.ShareId, x.FilePath)).ToList();
        }

        public async Task<HashSet<string>> GetReferencedStoragePathsUnderShardAsync(
            string shardPrefix, CancellationToken cancellationToken = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var paths = await db.Set<FileVersion>()
                .Where(v => v.StoragePath.StartsWith(shardPrefix))
                .Select(v => v.StoragePath)
                .Distinct()
                .ToListAsync(cancellationToken);

            return paths.ToHashSet(StringComparer.Ordinal);
        }

        public async Task<List<FileVersion>> DeletePathAsync(Guid shareId, string path)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var prefix = path.Length == 0 ? "" : path + "/";
            var query = db.Set<FileVersion>().Where(v => v.ShareId == shareId);
            query = path.Length == 0
                ? query
                : query.Where(v => v.FilePath == path || v.FilePath.StartsWith(prefix));

            var removed = await query.ToListAsync();
            if (removed.Count == 0) return removed;

            db.Set<FileVersion>().RemoveRange(removed);
            await db.SaveChangesAsync();
            return removed;
        }

        public async Task<List<FileVersion>> RenamePathAsync(
            Guid shareId,
            string oldPath,
            string newPath,
            Guid? sambaLifecycleEventId = null)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            // Serializable isolation also closes the rare overlap where an
            // expired event lease is reclaimed while the prior worker commits.
            await using var transaction = sambaLifecycleEventId.HasValue
                ? await db.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable)
                : null;
            SambaLifecycleEventReceipt? receipt = null;
            if (sambaLifecycleEventId.HasValue)
            {
                receipt = await db.SambaLifecycleEventReceipts.SingleOrDefaultAsync(
                    eventReceipt => eventReceipt.EventId == sambaLifecycleEventId.Value);
                if (receipt is null ||
                    !StringComparer.Ordinal.Equals(receipt.EventType, "rename"))
                {
                    throw new InvalidOperationException(
                        $"Samba rename event {sambaLifecycleEventId.Value:N} has no matching receipt.");
                }

                // The version rows and this checkpoint are committed together.
                // A retry after any later lifecycle effect failed must never
                // reinterpret the now-empty source as a fresh replace rename.
                if (receipt.RenameVersionsCompletedAtUtc.HasValue)
                {
                    await transaction!.CommitAsync();
                    return [];
                }
            }

            var oldPrefix = oldPath.Length == 0 ? "" : oldPath + "/";
            var newPrefix = newPath.Length == 0 ? "" : newPath + "/";

            var sourceQuery = db.Set<FileVersion>().Where(v => v.ShareId == shareId);
            sourceQuery = oldPath.Length == 0
                ? sourceQuery
                : sourceQuery.Where(v => v.FilePath == oldPath || v.FilePath.StartsWith(oldPrefix));
            var source = await sourceQuery.ToListAsync();

            var sourceIds = source.Select(v => v.Id).ToHashSet();
            var destinationQuery = db.Set<FileVersion>().Where(v => v.ShareId == shareId);
            destinationQuery = newPath.Length == 0
                ? destinationQuery
                : destinationQuery.Where(v => v.FilePath == newPath || v.FilePath.StartsWith(newPrefix));
            var displaced = await destinationQuery
                .Where(v => !sourceIds.Contains(v.Id))
                .ToListAsync();

            if (displaced.Count > 0)
            {
                db.Set<FileVersion>().RemoveRange(displaced);
                await db.SaveChangesAsync();
            }

            foreach (var version in source)
            {
                version.FilePath = version.FilePath == oldPath
                    ? newPath
                    : newPath + version.FilePath.Substring(oldPath.Length);
            }

            if (source.Count > 0)
                await db.SaveChangesAsync();

            if (receipt is not null)
            {
                receipt.RenameVersionsCompletedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync();
                await transaction!.CommitAsync();
            }

            return displaced;
        }

        public Task<List<FileVersion>> DeleteShareAsync(Guid shareId)
            => DeletePathAsync(shareId, "");

        public async Task<bool> IsStoragePathReferencedAsync(string storagePath)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.Set<FileVersion>().AnyAsync(v => v.StoragePath == storagePath);
        }

        public async Task<bool> ExistsWithHashAsync(Guid shareId, string filePath, string contentHash)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            // Check if the LATEST version of this file already has this hash.
            // We only check the latest older versions may share the hash but
            // we still want to skip if nothing changed since last write.
            var latestHash = await db.Set<FileVersion>()
                .Where(v => v.ShareId == shareId && v.FilePath == filePath)
                .OrderByDescending(v => v.SnapshotTimestampUtc)
                .Select(v => v.ContentHash)
                .FirstOrDefaultAsync();

            return latestHash == contentHash;
        }
    }
}
