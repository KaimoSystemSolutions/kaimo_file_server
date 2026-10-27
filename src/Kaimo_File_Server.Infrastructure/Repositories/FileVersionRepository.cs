using Microsoft.EntityFrameworkCore;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Persistence;

namespace Kaimo_File_Server.Infrastructure.Repositories
{
    public partial class FileVersionRepository : IFileVersionRepository
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

        public async Task<List<FileVersion>> DeleteByIdsAsync(IReadOnlyCollection<Guid> ids)
        {
            if (ids.Count == 0) return [];
            await using var db = await _dbFactory.CreateDbContextAsync();

            var idList = ids.ToList();
            var removed = await db.Set<FileVersion>().Where(v => idList.Contains(v.Id)).ToListAsync();
            if (removed.Count == 0) return removed;

            db.Set<FileVersion>().RemoveRange(removed);
            await db.SaveChangesAsync();
            return removed;
        }

        public async Task<List<Guid>> GetShareIdsWithVersionsAsync(CancellationToken cancellationToken = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            return await db.Set<FileVersion>()
                .Select(v => v.ShareId)
                .Distinct()
                .ToListAsync(cancellationToken);
        }

        public async Task<List<string>> GetVersionedPathsPageAsync(
            Guid shareId, string? afterPath, int take, CancellationToken cancellationToken = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            var query = db.Set<FileVersion>().Where(v => v.ShareId == shareId);
            if (afterPath is not null)
                query = query.Where(v => string.Compare(v.FilePath, afterPath) > 0);

            // Comparison and ordering both run in the database, so the cursor follows one
            // collation consistently whatever that collation is.
            return await query
                .Select(v => v.FilePath)
                .Distinct()
                .OrderBy(p => p)
                .Take(take)
                .ToListAsync(cancellationToken);
        }

        public async Task<List<FileVersion>> GetBudgetPruneCandidatesAsync(
            IReadOnlyCollection<Guid> shareIds, int take, CancellationToken cancellationToken = default)
        {
            if (shareIds.Count == 0 || take <= 0) return [];
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            var ids = shareIds.ToList();
            var versions = db.Set<FileVersion>();
            return await versions
                .Where(v => ids.Contains(v.ShareId)
                            && versions.Any(newer => newer.ShareId == v.ShareId
                                                     && newer.FilePath == v.FilePath
                                                     && newer.SnapshotTimestampUtc > v.SnapshotTimestampUtc))
                .OrderBy(v => v.SnapshotTimestampUtc)
                .Take(take)
                .ToListAsync(cancellationToken);
        }

        public async Task<List<FileVersion>> GetVersionsForPathsAsync(
            Guid shareId, IReadOnlyCollection<string> filePaths, CancellationToken cancellationToken = default)
        {
            if (filePaths.Count == 0) return [];
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            var paths = filePaths.ToList();
            return await db.Set<FileVersion>()
                .Where(v => v.ShareId == shareId && paths.Contains(v.FilePath))
                .OrderByDescending(v => v.SnapshotTimestampUtc)
                .ToListAsync(cancellationToken);
        }

        public async Task<List<string>> GetHistoryOnlyStoragePathsAsync(
            IReadOnlyCollection<Guid> shareIds, CancellationToken cancellationToken = default)
        {
            if (shareIds.Count == 0) return [];
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            var ids = shareIds.ToList();
            var versions = db.Set<FileVersion>();
            var inShares = versions.Where(v => ids.Contains(v.ShareId));
            var newestBlobs = inShares
                .Where(v => !versions.Any(newer => newer.ShareId == v.ShareId
                                                   && newer.FilePath == v.FilePath
                                                   && newer.SnapshotTimestampUtc > v.SnapshotTimestampUtc))
                .Select(v => v.StoragePath);

            return await inShares
                .Where(v => !newestBlobs.Contains(v.StoragePath))
                .Select(v => v.StoragePath)
                .Distinct()
                .ToListAsync(cancellationToken);
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
