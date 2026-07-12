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
        private readonly ApplicationDbContext _db;
        public RoleRepository(ApplicationDbContext db) { _db = db; }

        public async Task<Role?> GetByIdAsync(Guid id) => await _db.Roles.FindAsync(id);

        public async Task<Role?> GetByNameAsync(string name)
            => await _db.Roles.FirstOrDefaultAsync(r => r.Name == name);

        public async Task<IEnumerable<Role>> GetAllAsync() => await _db.Roles.ToListAsync();

        public async Task<Role> CreateAsync(Role role)
        {
            _db.Roles.Add(role);
            await _db.SaveChangesAsync();
            return role;
        }

        public async Task UpdateAsync(Role role)
        {
            _db.Roles.Update(role);
            await _db.SaveChangesAsync();
        }

        public async Task DeleteAsync(Guid id)
        {
            var role = await _db.Roles.FindAsync(id);
            if (role != null)
            {
                _db.Roles.Remove(role);
                await _db.SaveChangesAsync();
            }
        }
    }
}