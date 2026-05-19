using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

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
}