using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Repositories
{
    public class UserRepository : IUserRepository
    {
        private readonly ApplicationDbContext _db;
        public UserRepository(ApplicationDbContext db) { _db = db; }

        public async Task<User?> GetByIdAsync(Guid id) => await _db.Users.FindAsync(id);
        public async Task<User?> GetByUsernameAsync(string username)
            => await _db.Users.FirstOrDefaultAsync(u => u.Username == username);
        public async Task<IEnumerable<User>> GetAllAsync() => await _db.Users.ToListAsync();
        public async Task<User> CreateAsync(User user) { _db.Users.Add(user); await _db.SaveChangesAsync(); return user; }
        public async Task UpdateAsync(User user) { _db.Users.Update(user); await _db.SaveChangesAsync(); }
        public async Task DeleteAsync(Guid id)
        {
            var user = await _db.Users.FindAsync(id);
            if (user != null) { _db.Users.Remove(user); await _db.SaveChangesAsync(); }
        }
    }

    public class GroupRepository : IGroupRepository
    {
        private readonly ApplicationDbContext _db;
        public GroupRepository(ApplicationDbContext db) { _db = db; }

        public async Task<Group?> GetByIdAsync(Guid id) => await _db.Groups.FindAsync(id);
        public async Task<IEnumerable<Group>> GetAllAsync() => await _db.Groups.ToListAsync();
        public async Task<Group> CreateAsync(Group group)
        { _db.Groups.Add(group); await _db.SaveChangesAsync(); return group; }
        public async Task DeleteAsync(Guid id)
        {
            var group = await _db.Groups.FindAsync(id);
            if (group != null) { _db.Groups.Remove(group); await _db.SaveChangesAsync(); }
        }
    }

    public class RoleRepository : IRoleRepository
    {
        private readonly ApplicationDbContext _db;
        public RoleRepository(ApplicationDbContext db) { _db = db; }

        public async Task<Role?> GetByIdAsync(Guid id) => await _db.Roles.FindAsync(id);
        public async Task<IEnumerable<Role>> GetAllAsync() => await _db.Roles.ToListAsync();
        public async Task<Role> CreateAsync(Role role)
        { _db.Roles.Add(role); await _db.SaveChangesAsync(); return role; }
        public async Task DeleteAsync(Guid id)
        {
            var role = await _db.Roles.FindAsync(id);
            if (role != null) { _db.Roles.Remove(role); await _db.SaveChangesAsync(); }
        }
    }

    public class ShareRepository : IShareRepository
    {
        private readonly ApplicationDbContext _db;
        public ShareRepository(ApplicationDbContext db) { _db = db; }

        public async Task<List<ShareDefinition>> GetAllEnabledAsync()
            => await _db.ShareDefinitions.Where(s => s.IsEnabled).ToListAsync();
        public async Task<ShareDefinition?> GetByNameAsync(string name)
            => await _db.ShareDefinitions.FirstOrDefaultAsync(s => s.Name == name);
        public async Task<ShareDefinition> CreateAsync(ShareDefinition share)
        { _db.ShareDefinitions.Add(share); await _db.SaveChangesAsync(); return share; }
        public async Task DeleteAsync(Guid id)
        {
            var share = await _db.ShareDefinitions.FindAsync(id);
            if (share != null) { _db.ShareDefinitions.Remove(share); await _db.SaveChangesAsync(); }
        }
    }

    public class ShareAccessRepository : IShareAccessRepository
    {
        private readonly ApplicationDbContext _db;
        public ShareAccessRepository(ApplicationDbContext db) { _db = db; }

        public async Task<bool> HasAccessAsync(string shareName, Guid principalId)
            => await _db.ShareAccessEntries.AnyAsync(e => e.ShareName == shareName && e.PrincipalId == principalId);
        public async Task<List<ShareAccessEntry>> GetByShareAsync(string shareName)
            => await _db.ShareAccessEntries.Where(e => e.ShareName == shareName).ToListAsync();

        public async Task GrantAccessAsync(string shareName, Guid principalId)
        {
            var exists = await _db.ShareAccessEntries
                .AnyAsync(e => e.ShareName == shareName && e.PrincipalId == principalId);

            if (!exists)
            {
                _db.ShareAccessEntries.Add(new ShareAccessEntry(shareName, principalId));
                await _db.SaveChangesAsync();
            }
        }
    }
}
