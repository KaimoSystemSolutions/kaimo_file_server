using Kaimo_File_Server.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Repositories
{
    public partial class FileVersionRepository
    {
        public async Task<Dictionary<string, DateTime>> GetMissingSinceAsync(
            Guid shareId, CancellationToken cancellationToken = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            return await db.Set<FileVersionMissingPath>()
                .Where(m => m.ShareId == shareId)
                .ToDictionaryAsync(m => m.FilePath, m => m.MissingSinceUtc, StringComparer.Ordinal, cancellationToken);
        }

        public async Task ReplaceMissingSinceAsync(
            Guid shareId, IReadOnlyDictionary<string, DateTime> missingSince,
            CancellationToken cancellationToken = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var rows = db.Set<FileVersionMissingPath>();
            var existing = await rows.Where(m => m.ShareId == shareId).ToListAsync(cancellationToken);

            // Only the difference is written: a stable set of missing files costs no writes.
            foreach (var row in existing)
            {
                if (!missingSince.TryGetValue(row.FilePath, out var since)) rows.Remove(row);
                else if (row.MissingSinceUtc != since) row.MissingSinceUtc = since;
            }
            var known = existing.Select(m => m.FilePath).ToHashSet(StringComparer.Ordinal);
            rows.AddRange(missingSince
                .Where(m => !known.Contains(m.Key))
                .Select(m => new FileVersionMissingPath { ShareId = shareId, FilePath = m.Key, MissingSinceUtc = m.Value }));

            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
