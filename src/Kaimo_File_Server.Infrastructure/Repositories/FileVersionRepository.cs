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

        public async Task<List<FileVersion>> GetVersionsAsync(string filePath)
        {
            return await _db.Set<FileVersion>()
                .Where(v => v.FilePath == filePath)
                .OrderByDescending(v => v.SnapshotTimestampUtc)
                .ToListAsync();
        }

        public async Task<FileVersion?> GetVersionAsync(string filePath, DateTime snapshotTimestampUtc)
        {
            return await _db.Set<FileVersion>()
                .FirstOrDefaultAsync(v =>
                    v.FilePath == filePath &&
                    v.SnapshotTimestampUtc == snapshotTimestampUtc);
        }

        public async Task<List<DateTime>> GetSnapshotTimestampsAsync(string filePath)
        {
            return await _db.Set<FileVersion>()
                .Where(v => v.FilePath == filePath)
                .Select(v => v.SnapshotTimestampUtc)
                .Distinct()
                .OrderByDescending(t => t)
                .ToListAsync();
        }

        public async Task<List<DateTime>> GetAllSnapshotTimestampsAsync(string pathPrefix = "")
        {
            var query = _db.Set<FileVersion>().AsQueryable();

            if (!string.IsNullOrEmpty(pathPrefix))
                query = query.Where(v => v.FilePath.StartsWith(pathPrefix));

            return await query
                .Select(v => v.SnapshotTimestampUtc)
                .Distinct()
                .OrderByDescending(t => t)
                .ToListAsync();
        }

        public async Task<FileVersion> CreateAsync(FileVersion version)
        {
            _db.Set<FileVersion>().Add(version);
            await _db.SaveChangesAsync();
            return version;
        }

        public async Task<int> GetMaxVersionNumberAsync(string filePath)
        {
            var max = await _db.Set<FileVersion>()
                .Where(v => v.FilePath == filePath)
                .MaxAsync(v => (int?)v.VersionNumber);
            return max ?? 0;
        }

        public async Task<int> DeleteOlderThanAsync(string filePath, DateTime cutoff)
        {
            var toDelete = await _db.Set<FileVersion>()
                .Where(v => v.FilePath == filePath && v.SnapshotTimestampUtc < cutoff)
                .ToListAsync();

            _db.Set<FileVersion>().RemoveRange(toDelete);
            await _db.SaveChangesAsync();
            return toDelete.Count;
        }

        public async Task<int> TrimToMaxVersionsAsync(string filePath, int maxCount)
        {
            // Get IDs of versions to keep (newest N)
            var keepIds = await _db.Set<FileVersion>()
                .Where(v => v.FilePath == filePath)
                .OrderByDescending(v => v.SnapshotTimestampUtc)
                .Take(maxCount)
                .Select(v => v.Id)
                .ToListAsync();

            var toDelete = await _db.Set<FileVersion>()
                .Where(v => v.FilePath == filePath && !keepIds.Contains(v.Id))
                .ToListAsync();

            if (toDelete.Count == 0) return 0;

            _db.Set<FileVersion>().RemoveRange(toDelete);
            await _db.SaveChangesAsync();
            return toDelete.Count;
        }

        public async Task<bool> ExistsWithHashAsync(string filePath, string contentHash)
        {
            // Check if the LATEST version of this file already has this hash.
            // We only check the latest older versions may share the hash but
            // we still want to skip if nothing changed since last write.
            var latestHash = await _db.Set<FileVersion>()
                .Where(v => v.FilePath == filePath)
                .OrderByDescending(v => v.SnapshotTimestampUtc)
                .Select(v => v.ContentHash)
                .FirstOrDefaultAsync();

            return latestHash == contentHash;
        }
    }
}