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
        public async Task<List<Group>> GetGroupsForUserAsync(Guid userId)
        {
            return await _db.UserGroups
                .Where(ug => ug.UserId == userId)
                .Join(_db.Groups, ug => ug.GroupId, g => g.Id, (_, g) => g)
                .OrderBy(g => g.Name)
                .ToListAsync();
        }

        public async Task<List<Role>> GetRolesForUserAsync(Guid userId)
        {
            return await _db.UserRoles
                .Where(ur => ur.UserId == userId)
                .Join(_db.Roles, ur => ur.RoleId, r => r.Id, (_, r) => r)
                .OrderBy(r => r.Name)
                .ToListAsync();
        }

        public async Task SetGroupsForUserAsync(Guid userId, List<Guid> groupIds)
        {
            var existing = _db.UserGroups.Where(ug => ug.UserId == userId);
            _db.UserGroups.RemoveRange(existing);
            _db.UserGroups.AddRange(groupIds.Select(gId => new UserGroup(userId, gId)));
            await _db.SaveChangesAsync();
        }

        public async Task SetRolesForUserAsync(Guid userId, List<Guid> roleIds)
        {
            var existing = _db.UserRoles.Where(ur => ur.UserId == userId);
            _db.UserRoles.RemoveRange(existing);
            _db.UserRoles.AddRange(roleIds.Select(rId => new UserRole(userId, rId)));
            await _db.SaveChangesAsync();
        }

        public async Task UpdateNameAsync(Guid userId, string newName)
        {
            await _db.Users
                .Where(u => u.Id == userId)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.Name, newName));
        }
        public async Task UpdatePasswordAsync(Guid userId, string passwordHash, string ntHash)
        {
            await _db.Users
                .Where(u => u.Id == userId)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(x => x.PasswordHash, passwordHash)
                    .SetProperty(x => x.NtHash, ntHash));
        }
    }

    public class GroupRepository : IGroupRepository
    {
        private readonly ApplicationDbContext _db;
        public GroupRepository(ApplicationDbContext db) { _db = db; }

        public async Task<Group?> GetByIdAsync(Guid id) => await _db.Groups.FindAsync(id);
        
        public async Task<IEnumerable<Group>> GetAllAsync() => await _db.Groups.ToListAsync();
        
        public async Task<Group> CreateAsync(Group group)
        { 
            _db.Groups.Add(group); 
            await _db.SaveChangesAsync(); 
            return group; 
        }
        
        public async Task DeleteAsync(Guid id)
        {
            var group = await _db.Groups.FindAsync(id);
            if (group != null) { _db.Groups.Remove(group); await _db.SaveChangesAsync(); }
        }

        public async Task<List<User>> GetMembersAsync(Guid groupId)
        {
            return await _db.UserGroups
                .Where(ug => ug.GroupId == groupId)
                .Join(_db.Users, ug => ug.UserId, u => u.Id, (_, u) => u)
                .OrderBy(u => u.Name)
                .ToListAsync();
        }

        public async Task SetMembersAsync(Guid groupId, List<Guid> userIds)
        {
            var existing = _db.UserGroups.Where(ug => ug.GroupId == groupId);
            _db.UserGroups.RemoveRange(existing);
            _db.UserGroups.AddRange(userIds.Select(uId => new UserGroup(uId, groupId)));
            await _db.SaveChangesAsync();
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

        public async Task<List<ShareDefinition>> GetAllAsync()
            => await _db.ShareDefinitions.ToListAsync();

        public async Task<ShareDefinition?> GetByNameAsync(string name)
            => await _db.ShareDefinitions.FirstOrDefaultAsync(s => s.Name == name);

        public async Task<ShareDefinition?> GetByIdAsync(Guid id)
            => await _db.ShareDefinitions.FindAsync(id);

        public async Task<ShareDefinition> CreateAsync(ShareDefinition share)
        {
            _db.ShareDefinitions.Add(share);
            await _db.SaveChangesAsync();
            return share;
        }

        public async Task UpdateAsync(ShareDefinition share)
        {
            _db.ShareDefinitions.Update(share);
            await _db.SaveChangesAsync();
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

        public async Task RevokeAccessAsync(string shareName, Guid principalId)
        {
            var entry = await _db.ShareAccessEntries
                .FirstOrDefaultAsync(e => e.ShareName == shareName && e.PrincipalId == principalId);

            if (entry != null)
            {
                _db.ShareAccessEntries.Remove(entry);
                await _db.SaveChangesAsync();
            }
        }

        public async Task UpdateShareNameAsync(string oldName, string newName)
        {
            await _db.ShareAccessEntries
                .Where(e => e.ShareName == oldName)
                .ExecuteUpdateAsync(e => e.SetProperty(x => x.ShareName, newName));
        }
    }
}
