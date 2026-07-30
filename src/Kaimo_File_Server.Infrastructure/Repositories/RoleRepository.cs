// ============================================================================
// UPDATED RoleRepository — replace the existing one in Repositories.cs
// or extract into its own file.
// ============================================================================

using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Repositories
{
    public class RoleRepository : IRoleRepository
    {
        private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;

        public RoleRepository(IDbContextFactory<ApplicationDbContext> db) { _dbFactory = db; }

        public async Task<Role?> GetByIdAsync(Guid id)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.Roles.FindAsync(id);
        }

        public async Task<Role?> GetByNameAsync(string name)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.Roles.FirstOrDefaultAsync(r => r.Name == name);
        }


        public async Task<IEnumerable<Role>> GetAllAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.Roles.ToListAsync();
        } 

        public async Task<Role> CreateAsync(Role role)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            db.Roles.Add(role);
            await db.SaveChangesAsync();
            return role;
        }

        public async Task UpdateAsync(Role role)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            db.Roles.Update(role);
            await db.SaveChangesAsync();
        }

        public async Task DeleteAsync(Guid id)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var role = await db.Roles.FindAsync(id);
            if (role != null)
            {
                db.Roles.Remove(role);
                await db.SaveChangesAsync();
            }
        }
    }
}