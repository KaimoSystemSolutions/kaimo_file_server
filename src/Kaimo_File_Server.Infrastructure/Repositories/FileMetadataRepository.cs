using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Repositories;

public class FileMetadataRepository : IFileMetadataRepository
{
    private readonly ApplicationDbContext _db;

    public FileMetadataRepository(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<FileMetadata?> GetByPathAsync(string path)
    {
        var normalized = ShareRelativePath.Normalize(path);
        return await _db.FileMetadata
            .Include(m => m.Acl)
            .FirstOrDefaultAsync(m => m.Path == normalized);
    }

    public async Task<FileMetadata> GetOrCreateAsync(
        string path, bool isDirectory, Guid userId, Guid shareId)
    {
        var normalized = ShareRelativePath.Normalize(path);

        var existing = await _db.FileMetadata
            .Include(m => m.Acl)
            .FirstOrDefaultAsync(m => m.ShareId == shareId && m.Path == normalized);

        if (existing != null)
            return existing;

        var meta = new FileMetadata
        {
            Id = Guid.NewGuid(),
            ShareId = shareId,
            OwnerId = userId,
            Path = normalized,
            Name = ShareRelativePath.GetFileName(normalized),
            Size = 0,
            IsDirectory = isDirectory,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow
        };

        _db.FileMetadata.Add(meta);
        await _db.SaveChangesAsync();
        return meta;
    }
}