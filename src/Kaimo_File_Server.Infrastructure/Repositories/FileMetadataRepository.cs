using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Repositories
{
    public class FileMetadataRepository : IFileMetadataRepository
    {
        private readonly ApplicationDbContext _db;
        public FileMetadataRepository(ApplicationDbContext db) { _db = db; }

        public async Task<FileMetadata?> GetByPathAsync(string path)
            => await _db.FileMetadata
                .Include(m => m.Acl)
                .FirstOrDefaultAsync(m => m.Path == path);

        public async Task<FileMetadata> GetOrCreateAsync(string path, bool isDirectory, Guid userId, Guid shareId)
        {
            var existing = await _db.FileMetadata
                .Include(m => m.Acl)
                .FirstOrDefaultAsync(m => m.ShareId == shareId && m.Path == path);

            if (existing != null)
                return existing;

            var fileName = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
            if (string.IsNullOrEmpty(fileName)) fileName = "(root)";

            var meta = new FileMetadata
            {
                Id = Guid.NewGuid(),
                ShareId = shareId,
                OwnerId = userId,
                Path = path,
                Name = fileName,
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
}