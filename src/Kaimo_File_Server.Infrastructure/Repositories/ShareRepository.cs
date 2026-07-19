using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Repositories;

    /// <summary>
    /// EF Core implementation of <see cref="IShareRepository"/>.
    /// </summary>
    public class ShareRepository : IShareRepository
    {
        private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;

        public ShareRepository(IDbContextFactory<ApplicationDbContext> dbFactory)
        {
            _dbFactory = dbFactory;
        }

        public async Task<List<ShareDefinition>> GetAllEnabledAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.ShareDefinitions.Where(s => s.IsEnabled).ToListAsync();
        }

        public async Task<List<ShareDefinition>> GetAllAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.ShareDefinitions.ToListAsync();
        }

        public async Task<ShareDefinition?> GetByNameAsync(string name)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.ShareDefinitions.FirstOrDefaultAsync(s => s.Name == name);
        }

        public async Task<ShareDefinition?> GetByIdAsync(Guid id)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.ShareDefinitions.FindAsync(id);
        }

        public async Task<ShareDefinition> CreateAsync(ShareDefinition share)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            db.ShareDefinitions.Add(share);
            await db.SaveChangesAsync();
            return share;
        }

        public async Task UpdateAsync(ShareDefinition share)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            db.ShareDefinitions.Update(share);
            await db.SaveChangesAsync();
        }

        public async Task DeleteAsync(Guid id)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var share = await db.ShareDefinitions.FindAsync(id);
            if (share != null)
            {
                var shareAssignments = await db.ScopedRoleAssignments
                    .Where(a => a.ScopeType == Kaimo_File_Server.Core.Security.ScopeType.Share
                                && a.ScopeId == id)
                    .ToListAsync();
                db.ScopedRoleAssignments.RemoveRange(shareAssignments);
                db.ShareDefinitions.Remove(share);
                await db.SaveChangesAsync();
            }
        }
    }
