using Kaimo_File_Server_Core.Core.Domain;
using Kaimo_File_Server_Core.Core.Repositories;
using Kaimo_File_Server_Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;


namespace Kaimo_File_Server_Core.Infrastructure.Repositories
{
    public class ShareAccessRepository : IShareAccessRepository
    {
        private readonly ApplicationDbContext _db;

        public ShareAccessRepository(ApplicationDbContext db)
        {
            _db = db;
        }

        public async Task<bool> HasAccessAsync(string shareName, Guid principalId)
            => await EntityFrameworkQueryableExtensions.AnyAsync(
                _db.ShareAccessEntries,
                e => e.ShareName == shareName && e.PrincipalId == principalId);

        public async Task<List<ShareAccessEntry>> GetByShareAsync(string shareName)
            => await EntityFrameworkQueryableExtensions.ToListAsync(
                _db.ShareAccessEntries.Where(e => e.ShareName == shareName));
    }
}
