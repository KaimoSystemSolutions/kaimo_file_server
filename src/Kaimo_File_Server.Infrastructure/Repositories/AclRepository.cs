using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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

        var oldNormalized = ShareRelativePath.Normalize(oldRelativePath);
        var newNormalized = ShareRelativePath.Normalize(newRelativePath);

        // Exakter Treffer (Datei oder Ordner selbst)
        // + alle Kindpfade (beginnen mit oldPath + "/")
        var prefix = oldNormalized + "/";

        var affected = await _db.FileMetadata
            .Where(m => m.ShareId == shareId &&
                         (m.Path == oldNormalized || m.Path.StartsWith(prefix)))
            .ToListAsync();

        foreach (var meta in affected)
        {
            if (meta.Path == oldNormalized)
            {
                meta.Path = newNormalized;
            }
            else
            {
                // Kind: alten Prefix durch neuen ersetzen
                meta.Path = newNormalized + meta.Path.Substring(oldNormalized.Length);
            }
        }

        await _db.SaveChangesAsync();
    }

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