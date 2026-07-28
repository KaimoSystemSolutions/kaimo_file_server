using Microsoft.EntityFrameworkCore;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Persistence;

namespace Kaimo_File_Server.Infrastructure.Repositories
{
    public class FileVersionRepository : IFileVersionRepository
    {
        private readonly ApplicationDbContext _db;

        public FileVersionRepository(ApplicationDbContext db)
        {
            _db = db;
        }

        public async Task<List<FileVersion>> GetVersionsAsync(Guid shareId, string filePath)
        {
            return await _db.Set<FileVersion>()
                .Where(v => v.ShareId == shareId && v.FilePath == filePath)
                .OrderByDescending(v => v.SnapshotTimestampUtc)
                .ToListAsync();
        }

        public async Task<FileVersion?> GetVersionAsync(Guid shareId, string filePath, DateTime snapshotTimestampUtc)
        {
            return await _db.Set<FileVersion>()
                .FirstOrDefaultAsync(v =>
                    v.ShareId == shareId &&
                    v.FilePath == filePath &&
                    v.SnapshotTimestampUtc == snapshotTimestampUtc);
        }

        public async Task<List<DateTime>> GetSnapshotTimestampsAsync(Guid shareId, string filePath)
        {
            return await _db.Set<FileVersion>()
                .Where(v => v.ShareId == shareId && v.FilePath == filePath)
                .Select(v => v.SnapshotTimestampUtc)
                .Distinct()
                .OrderByDescending(t => t)
                .ToListAsync();
        }

        public async Task<List<DateTime>> GetAllSnapshotTimestampsAsync(Guid shareId, string pathPrefix = "")
        {
            var query = _db.Set<FileVersion>()
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
            var query = _db.Set<FileVersion>()
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
            _db.Set<FileVersion>().Add(version);
            await _db.SaveChangesAsync();
            return version;
        }

        public async Task<int> GetMaxVersionNumberAsync(Guid shareId, string filePath)
        {
            var max = await _db.Set<FileVersion>()
                .Where(v => v.ShareId == shareId && v.FilePath == filePath)
                .MaxAsync(v => (int?)v.VersionNumber);
            return max ?? 0;
        }

        public async Task<List<FileVersion>> DeleteOlderThanAsync(Guid shareId, string filePath, DateTime cutoff)
        {
            var toDelete = await _db.Set<FileVersion>()
                .Where(v => v.ShareId == shareId && v.FilePath == filePath && v.SnapshotTimestampUtc < cutoff)
                .ToListAsync();

            _db.Set<FileVersion>().RemoveRange(toDelete);
            await _db.SaveChangesAsync();
            return toDelete;
        }

        public async Task<List<FileVersion>> TrimToMaxVersionsAsync(Guid shareId, string filePath, int maxCount)
        {
            // Get IDs of versions to keep (newest N)
            var keepIds = await _db.Set<FileVersion>()
                .Where(v => v.ShareId == shareId && v.FilePath == filePath)
                .OrderByDescending(v => v.SnapshotTimestampUtc)
                .Take(maxCount)
                .Select(v => v.Id)
                .ToListAsync();

            var toDelete = await _db.Set<FileVersion>()
                .Where(v => v.ShareId == shareId && v.FilePath == filePath && !keepIds.Contains(v.Id))
                .ToListAsync();

            if (toDelete.Count == 0) return [];

            _db.Set<FileVersion>().RemoveRange(toDelete);
            await _db.SaveChangesAsync();
            return toDelete;
        }

        public async Task<List<FileVersion>> DeletePathAsync(Guid shareId, string path)
        {
            var prefix = path.Length == 0 ? "" : path + "/";
            var query = _db.Set<FileVersion>().Where(v => v.ShareId == shareId);
            query = path.Length == 0
                ? query
                : query.Where(v => v.FilePath == path || v.FilePath.StartsWith(prefix));

            var removed = await query.ToListAsync();
            if (removed.Count == 0) return removed;

            _db.Set<FileVersion>().RemoveRange(removed);
            await _db.SaveChangesAsync();
            return removed;
        }

        public async Task<List<FileVersion>> RenamePathAsync(
            Guid shareId, string oldPath, string newPath)
        {
            var oldPrefix = oldPath.Length == 0 ? "" : oldPath + "/";
            var newPrefix = newPath.Length == 0 ? "" : newPath + "/";

            var sourceQuery = _db.Set<FileVersion>().Where(v => v.ShareId == shareId);
            sourceQuery = oldPath.Length == 0
                ? sourceQuery
                : sourceQuery.Where(v => v.FilePath == oldPath || v.FilePath.StartsWith(oldPrefix));
            var source = await sourceQuery.ToListAsync();

            var sourceIds = source.Select(v => v.Id).ToHashSet();
            var destinationQuery = _db.Set<FileVersion>().Where(v => v.ShareId == shareId);
            destinationQuery = newPath.Length == 0
                ? destinationQuery
                : destinationQuery.Where(v => v.FilePath == newPath || v.FilePath.StartsWith(newPrefix));
            var displaced = await destinationQuery
                .Where(v => !sourceIds.Contains(v.Id))
                .ToListAsync();

            if (displaced.Count > 0)
            {
                _db.Set<FileVersion>().RemoveRange(displaced);
                await _db.SaveChangesAsync();
            }

            foreach (var version in source)
            {
                version.FilePath = version.FilePath == oldPath
                    ? newPath
                    : newPath + version.FilePath.Substring(oldPath.Length);
            }

            if (source.Count > 0)
                await _db.SaveChangesAsync();

            return displaced;
        }

        public Task<List<FileVersion>> DeleteShareAsync(Guid shareId)
            => DeletePathAsync(shareId, "");

        public Task<bool> IsStoragePathReferencedAsync(string storagePath)
            => _db.Set<FileVersion>().AnyAsync(v => v.StoragePath == storagePath);

        public async Task<bool> ExistsWithHashAsync(Guid shareId, string filePath, string contentHash)
        {
            // Check if the LATEST version of this file already has this hash.
            // We only check the latest older versions may share the hash but
            // we still want to skip if nothing changed since last write.
            var latestHash = await _db.Set<FileVersion>()
                .Where(v => v.ShareId == shareId && v.FilePath == filePath)
                .OrderByDescending(v => v.SnapshotTimestampUtc)
                .Select(v => v.ContentHash)
                .FirstOrDefaultAsync();

            return latestHash == contentHash;
        }
    }
}
