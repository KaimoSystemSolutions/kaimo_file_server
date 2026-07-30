using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Repositories;

public class FileMetadataRepository : IFileMetadataRepository
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;

    public FileMetadataRepository(IDbContextFactory<ApplicationDbContext> db)
    {
        _dbFactory = db;
    }

    public async Task<FileMetadata?> GetByPathAsync(string path)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var normalized = ShareRelativePath.Normalize(path);
        return await db.FileMetadata
            .Include(m => m.Acl)
            .FirstOrDefaultAsync(m => m.Path == normalized);
    }

    public async Task<FileMetadata> GetOrCreateAsync(
        string path, bool isDirectory, Guid userId, Guid shareId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var normalized = ShareRelativePath.Normalize(path);

        var existing = await db.FileMetadata
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

        db.FileMetadata.Add(meta);
        await db.SaveChangesAsync();
        return meta;
    }
}