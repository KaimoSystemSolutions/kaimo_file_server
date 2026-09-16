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
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;

    public AclRepository(IDbContextFactory<ApplicationDbContext> db)
    {
        _dbFactory = db;
    }

    public async Task<List<AccessEntry>> GetByFileMetadataIdAsync(Guid fileMetadataId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        return await db.AccessEntries
            .Where(e => e.FileMetadataId == fileMetadataId)
            .ToListAsync();
    }

    public async Task<AccessEntry> AddAsync(AccessEntry entry)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        db.AccessEntries.Add(entry);
        await db.SaveChangesAsync();
        return entry;
    }

    public async Task UpdateAsync(AccessEntry entry)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        db.AccessEntries.Update(entry);
        await db.SaveChangesAsync();
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
                // The internal attempt uses its own short-lived context, so there is no
                // stale tracker to reset here; just back off and re-read the rows.
                await Task.Delay(25 * attempt);
            }
        }
    }
    
    private async Task RenameFileMetadataPathsInternalAsync(Guid shareId, string oldRelativePath, string newRelativePath)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var oldNormalized = ShareRelativePath.Normalize(oldRelativePath);
        var newNormalized = ShareRelativePath.Normalize(newRelativePath);
        var prefix = oldNormalized + "/";

        var affected = await db.FileMetadata
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
        var displaced = await db.FileMetadata
            .Where(m => m.ShareId == shareId
                        && targetPaths.Contains(m.Path)
                        && !affectedIds.Contains(m.Id))
            .ToListAsync();

        if (displaced.Count > 0)
        {
            db.FileMetadata.RemoveRange(displaced);
            await db.SaveChangesAsync();
        }

        foreach (var meta in affected)
        {
            var targetPath = meta.Path == oldNormalized
                ? newNormalized
                : newNormalized + meta.Path.Substring(oldNormalized.Length);
            meta.Path = targetPath;
            meta.Name = ShareRelativePath.GetFileName(targetPath);
        }

        await db.SaveChangesAsync();
    }

    public async Task<int> DeleteFileMetadataPathsAsync(Guid shareId, string relativePath)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var normalized = ShareRelativePath.Normalize(relativePath);
        var prefix = normalized.Length == 0 ? "" : normalized + "/";

        var query = db.FileMetadata.Where(m => m.ShareId == shareId);
        query = normalized.Length == 0
            ? query
            : query.Where(m => m.Path == normalized || m.Path.StartsWith(prefix));

        // ExecuteDeleteAsync: a single set-based DELETE, no per-row change tracking or
        // materialization. Safe in demo mode even though it bypasses the
        // ReadOnlyDemoSaveInterceptor, because ReadOnlyDemoAclService.DeleteAclAsync
        // throws ReadOnlyDemoException before any call reaches this repository. The
        // cascade delete on AccessEntry removes the ACL rows for the deleted subtree.
        return await query.ExecuteDeleteAsync();
    }

    public Task<int> DeleteShareFileMetadataAsync(Guid shareId)
        => DeleteFileMetadataPathsAsync(shareId, "");

    public async Task DeleteAsync(Guid entryId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var entry = await db.AccessEntries.FindAsync(entryId);
        if (entry != null)
        {
            db.AccessEntries.Remove(entry);
            await db.SaveChangesAsync();
        }
    }

    public async Task<List<(string Path, bool IsDirectory, List<AccessEntry> Acl)>>
        GetAclsForPathsAsync(Guid shareId, List<string> paths)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        // Normalize all incoming paths to ensure they match DB format
        var normalizedPaths = paths
            .Select(ShareRelativePath.Normalize)
            .Distinct()
            .ToList();

        var metas = await db.FileMetadata
            .Where(m => m.ShareId == shareId && normalizedPaths.Contains(m.Path))
            .Include(m => m.Acl)
            .ToListAsync();

        return metas
            .Select(m => (m.Path, m.IsDirectory, m.Acl?.ToList() ?? new List<AccessEntry>()))
            .ToList();
    }

    public async Task<Dictionary<string, int>> GetAclCountsByPathAsync(Guid shareId, IEnumerable<string> paths)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var pathList = paths.ToList();

        return await db.FileMetadata
            .Where(fm => fm.ShareId == shareId && pathList.Contains(fm.Path))
            .Select(fm => new
            {
                fm.Path,
                Count = fm.Acl.Count
            })
            .ToDictionaryAsync(x => x.Path, x => x.Count);
    }
}
