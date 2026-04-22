using Kaimo_File_Server_Core.Core.Domain;
using Kaimo_File_Server_Core.Core.Repositories;
using Kaimo_File_Server_Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server_Core.Infrastructure.Repositories
{
    public class ShareRepository : IShareRepository
    {
        private readonly ApplicationDbContext _db;

        public ShareRepository(ApplicationDbContext db)
        {
            _db = db;
        }

        public async Task<List<ShareDefinition>> GetAllEnabledAsync()
            => await EntityFrameworkQueryableExtensions.ToListAsync(
                _db.ShareDefinitions.Where(s => s.IsEnabled));

        public async Task<ShareDefinition?> GetByNameAsync(string name)
            => await EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
                _db.ShareDefinitions, s => s.Name == name);

        public async Task<ShareDefinition> CreateAsync(ShareDefinition share)
        {
            _db.ShareDefinitions.Add(share);
            await _db.SaveChangesAsync();
            return share;
        }

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
