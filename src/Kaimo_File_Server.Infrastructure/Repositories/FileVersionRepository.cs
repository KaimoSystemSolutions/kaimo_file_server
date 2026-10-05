using Microsoft.EntityFrameworkCore;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Persistence;
using System.Data;
using Microsoft.Extensions.Logging;

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

        // Runs under the execution strategy, so a serialization failure (40001) or a
        // dropped connection replays the whole unit on a fresh context.
        public Task<List<FileVersion>> RenamePathAsync(
            Guid shareId,
            string oldPath,
            string newPath,
            Guid? sambaLifecycleEventId = null)
        {
            var plan = new RenamePlan();
            return _dbFactory.ExecuteResilientAsync(
                db => RenamePathCoreAsync(db, shareId, oldPath, newPath, sambaLifecycleEventId, plan));
        }

        /// <summary>
        /// The rows the first attempt of a rename without event receipt read before it wrote:
        /// source versions with their original paths, and the destination versions it displaces.
        /// </summary>
        private sealed class RenamePlan
        {
            public Dictionary<Guid, string>? SourcePaths;
            public List<FileVersion>? Displaced;
        }

        private static async Task<List<FileVersion>> RenamePathCoreAsync(
            ApplicationDbContext db,
            Guid shareId,
            string oldPath,
            string newPath,
            Guid? sambaLifecycleEventId,
            RenamePlan plan)
        {
            // One transaction, so a failure between removing the displaced rows and moving the
            // source never leaves half a rename behind. Serializable isolation also closes the
            // rare overlap where an expired event lease is reclaimed while the prior worker commits.
            await using var transaction = sambaLifecycleEventId.HasValue
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable)
                : await db.Database.BeginTransactionAsync();
            SambaLifecycleEventReceipt? receipt = null;
            if (sambaLifecycleEventId.HasValue)
            {
                // Row-lock the receipt as the very first statement. The bridge renews the
                // event lease on this row every 30 s; without the lock a renewal committing
                // mid-transaction fails the checkpoint write below with a serialization
                // error (40001), and a rename that always outlasts the renewal interval
                // would never get through. With the lock the renewal waits until commit, so
                // a conflict is only possible here, where a retry costs nothing.
                await db.SambaLifecycleEventReceipts
                    .Where(eventReceipt => eventReceipt.EventId == sambaLifecycleEventId.Value)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(eventReceipt => eventReceipt.LeaseUntilUtc,
                            eventReceipt => eventReceipt.LeaseUntilUtc));

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
                    await transaction.CommitAsync();
                    return [];
                }
            }

            List<FileVersion> source;
            List<FileVersion> displaced;
            Dictionary<Guid, string> sourcePaths;
            if (receipt is null && plan.SourcePaths is not null)
            {
                // Replay without a checkpoint. It may follow a COMMIT whose acknowledgement was
                // lost (versions already moved, displaced rows already gone) or a real rollback
                // (nothing applied); the current rows cannot tell the two apart. Re-applying the
                // first attempt's plan by row id is correct in both cases: moving a row that
                // already carries its new path and deleting a row that is gone change nothing.
                // The displaced versions are returned either way, so their blobs are removed.
                db.GetDatabaseLogger().LogWarning(
                    "Replaying version rename {OldPath} -> {NewPath} in share {ShareId} with the rows its first " +
                    "attempt read ({Moved} moved, {Displaced} displaced).",
                    oldPath, newPath, shareId, plan.SourcePaths.Count, plan.Displaced!.Count);
                sourcePaths = plan.SourcePaths;
                var sourceIds = sourcePaths.Keys.ToList();
                source = await db.Set<FileVersion>().Where(v => sourceIds.Contains(v.Id)).ToListAsync();
                var displacedIds = plan.Displaced.Select(v => v.Id).ToList();
                db.Set<FileVersion>().RemoveRange(
                    await db.Set<FileVersion>().Where(v => displacedIds.Contains(v.Id)).ToListAsync());
                await db.SaveChangesAsync();
                displaced = plan.Displaced;
            }
            else
            {
                var oldPrefix = oldPath.Length == 0 ? "" : oldPath + "/";
                var newPrefix = newPath.Length == 0 ? "" : newPath + "/";

                var sourceQuery = db.Set<FileVersion>().Where(v => v.ShareId == shareId);
                sourceQuery = oldPath.Length == 0
                    ? sourceQuery
                    : sourceQuery.Where(v => v.FilePath == oldPath || v.FilePath.StartsWith(oldPrefix));
                source = await sourceQuery.ToListAsync();
                sourcePaths = source.ToDictionary(v => v.Id, v => v.FilePath);

                var sourceIds = sourcePaths.Keys.ToHashSet();
                var destinationQuery = db.Set<FileVersion>().Where(v => v.ShareId == shareId);
                destinationQuery = newPath.Length == 0
                    ? destinationQuery
                    : destinationQuery.Where(v => v.FilePath == newPath || v.FilePath.StartsWith(newPrefix));
                displaced = await destinationQuery
                    .Where(v => !sourceIds.Contains(v.Id))
                    .ToListAsync();

                // Recorded before the first write, so it exists whenever a COMMIT may have happened.
                if (receipt is null)
                {
                    plan.SourcePaths = sourcePaths;
                    plan.Displaced = displaced;
                }

                if (displaced.Count > 0)
                {
                    db.Set<FileVersion>().RemoveRange(displaced);
                    await db.SaveChangesAsync();
                }
            }

            foreach (var version in source)
            {
                var original = sourcePaths[version.Id];
                version.FilePath = original == oldPath
                    ? newPath
                    : newPath + original.Substring(oldPath.Length);
            }

            if (source.Count > 0)
                await db.SaveChangesAsync();

            if (receipt is not null)
            {
                receipt.RenameVersionsCompletedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }

            await transaction.CommitAsync();
            return displaced;
        }

        public Task<List<FileVersion>> DeleteShareAsync(Guid shareId)
            => DeletePathAsync(shareId, "");

        public async Task<bool> IsStoragePathReferencedAsync(string storagePath)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.Set<FileVersion>().AnyAsync(v => v.StoragePath == storagePath);
        }

        public async Task<List<Guid>> GetShareIdsReferencingStoragePathAsync(string storagePath)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.Set<FileVersion>()
                .Where(v => v.StoragePath == storagePath)
                .Select(v => v.ShareId)
                .Distinct()
                .ToListAsync();
        }

        public async Task<List<(Guid ShareId, string StoragePath)>> GetAllBlobReferencesAsync(
            CancellationToken cancellationToken = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var rows = await db.Set<FileVersion>()
                .Select(v => new { v.ShareId, v.StoragePath })
                .Distinct()
                .ToListAsync(cancellationToken);
            return rows.Select(r => (r.ShareId, r.StoragePath)).ToList();
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
