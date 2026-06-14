using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Repositories
{
    /// <summary>
    /// EF Core implementation of <see cref="IShareRepository"/>.
    /// </summary>
    public class ShareRepository : IShareRepository
    {
        private readonly ApplicationDbContext _db;

        public ShareRepository(ApplicationDbContext db)
        {
            _db = db;
        }

        /// <inheritdoc />
        public async Task<List<ShareDefinition>> GetAllEnabledAsync()
            => await _db.ShareDefinitions.Where(s => s.IsEnabled).ToListAsync();

        /// <inheritdoc />
        public async Task<List<ShareDefinition>> GetAllAsync()
            => await _db.ShareDefinitions.ToListAsync();

        /// <inheritdoc />
        public async Task<ShareDefinition?> GetByNameAsync(string name)
            => await _db.ShareDefinitions.FirstOrDefaultAsync(s => s.Name == name);

        /// <inheritdoc />
        public async Task<ShareDefinition?> GetByIdAsync(Guid id)
            => await _db.ShareDefinitions.FindAsync(id);

        /// <inheritdoc />
        public async Task<ShareDefinition> CreateAsync(ShareDefinition share)
        {
            _db.ShareDefinitions.Add(share);
            //_db.ShareAccessEntries.AddRange(new ShareAccessEntry());

            await _db.SaveChangesAsync();
            return share;
        }

        /// <inheritdoc />
        public async Task UpdateAsync(ShareDefinition share)
        {
            _db.ShareDefinitions.Update(share);
            await _db.SaveChangesAsync();
        }

        /// <inheritdoc />
        public async Task DeleteAsync(Guid id)
        {
            var share = await _db.ShareDefinitions.FindAsync(id);
            if (share != null)
            {
                _db.ShareDefinitions.Remove(share);
                await _db.SaveChangesAsync();
            }
        }
    }
}