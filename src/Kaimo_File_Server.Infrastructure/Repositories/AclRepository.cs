using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Kaimo_File_Server.Infrastructure.Repositories;

public class AclRepository : IAclRepository
{
    private readonly ApplicationDbContext _db;

    public AclRepository(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<List<AccessEntry>> GetByFileMetadataIdAsync(Guid fileMetadataId)
    {
        return await _db.AccessEntries
            .Where(e => e.FileMetadataId == fileMetadataId)
            .ToListAsync();
    }

    public async Task<AccessEntry> AddAsync(AccessEntry entry)
    {
        _db.AccessEntries.Add(entry);
        await _db.SaveChangesAsync();
        return entry;
    }

    public async Task UpdateAsync(AccessEntry entry)
    {
        _db.AccessEntries.Update(entry);
        await _db.SaveChangesAsync();
    }

    public async Task RenameFileMetadataPathsAsync(Guid shareId, string oldRelativePath, string newRelativePath)
    {
        const int maxAttempts = 3;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await RenameFileMetadataPathsInternalAsync(shareId, oldRelativePath, newRelativePath);
                return;
            }
            catch (DbUpdateException ex) when (attempt < maxAttempts
                                               && ex.InnerException is PostgresException { SqlState: "23505" })
            {
                // Stale tracked entities from the failed attempt, plus a fresh look
                // at the (now possibly watcher-updated) rows.
                foreach (var entry in _db.ChangeTracker.Entries().ToList())
                    entry.State = EntityState.Detached;

                await Task.Delay(25 * attempt);
            }
        }
    }
    
    private async Task RenameFileMetadataPathsInternalAsync(Guid shareId, string oldRelativePath, string newRelativePath)
    {
        var oldNormalized = ShareRelativePath.Normalize(oldRelativePath);
        var newNormalized = ShareRelativePath.Normalize(newRelativePath);
        var prefix = oldNormalized + "/";

        var affected = await _db.FileMetadata
            .Where(m => m.ShareId == shareId &&
                        (m.Path == oldNormalized || m.Path.StartsWith(prefix)))
            .ToListAsync();

        if (affected.Count == 0)
            return;

        var affectedIds = affected.Select(m => m.Id).ToHashSet();
        var targetPaths = affected
            .Select(meta => meta.Path == oldNormalized
                ? newNormalized
                : newNormalized + meta.Path.Substring(oldNormalized.Length))
            .ToList();

        // A replace-style rename displaces the destination object. Keep the source
        // object's owner and ACL, and remove stale/watcher-created destination rows.
        var displaced = await _db.FileMetadata
            .Where(m => m.ShareId == shareId
                        && targetPaths.Contains(m.Path)
                        && !affectedIds.Contains(m.Id))
            .ToListAsync();

        if (displaced.Count > 0)
        {
            _db.FileMetadata.RemoveRange(displaced);
            await _db.SaveChangesAsync();
        }

        foreach (var meta in affected)
        {
            var targetPath = meta.Path == oldNormalized
                ? newNormalized
                : newNormalized + meta.Path.Substring(oldNormalized.Length);
            meta.Path = targetPath;
            meta.Name = ShareRelativePath.GetFileName(targetPath);
        }

        await _db.SaveChangesAsync();
    }

    public async Task<int> DeleteFileMetadataPathsAsync(Guid shareId, string relativePath)
    {
        var normalized = ShareRelativePath.Normalize(relativePath);
        var prefix = normalized.Length == 0 ? "" : normalized + "/";

        var query = _db.FileMetadata.Where(m => m.ShareId == shareId);
        query = normalized.Length == 0
            ? query
            : query.Where(m => m.Path == normalized || m.Path.StartsWith(prefix));

        var rows = await query.ToListAsync();
        if (rows.Count == 0) return 0;

        _db.FileMetadata.RemoveRange(rows);
        await _db.SaveChangesAsync();
        return rows.Count;
    }

    public Task<int> DeleteShareFileMetadataAsync(Guid shareId)
        => DeleteFileMetadataPathsAsync(shareId, "");

    public async Task DeleteAsync(Guid entryId)
    {
        var entry = await _db.AccessEntries.FindAsync(entryId);
        if (entry != null)
        {
            _db.AccessEntries.Remove(entry);
            await _db.SaveChangesAsync();
        }
    }

    public async Task<List<(string Path, bool IsDirectory, List<AccessEntry> Acl)>>
        GetAclsForPathsAsync(Guid shareId, List<string> paths)
    {
        // Normalize all incoming paths to ensure they match DB format
        var normalizedPaths = paths
            .Select(ShareRelativePath.Normalize)
            .Distinct()
            .ToList();

        var metas = await _db.FileMetadata
            .Where(m => m.ShareId == shareId && normalizedPaths.Contains(m.Path))
            .Include(m => m.Acl)
            .ToListAsync();

        return metas
            .Select(m => (m.Path, m.IsDirectory, m.Acl?.ToList() ?? new List<AccessEntry>()))
            .ToList();
    }

    public async Task<Dictionary<string, int>> GetAclCountsByPathAsync(Guid shareId, IEnumerable<string> paths)
    {
        var pathList = paths.ToList();

        return await _db.FileMetadata
            .Where(fm => fm.ShareId == shareId && pathList.Contains(fm.Path))
            .Select(fm => new
            {
                fm.Path,
                Count = fm.Acl.Count
            })
            .ToDictionaryAsync(x => x.Path, x => x.Count);
    }
}
