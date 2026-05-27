using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Services
{
    public class UserContextFactory : IUserContextFactory
    {
        private readonly ApplicationDbContext _db;
        public UserContextFactory(ApplicationDbContext db) { _db = db; }

        public async Task<UserContext> CreateAsync(User user)
        {
            var groups = await _db.UserGroups
                .Where(ug => ug.UserId == user.Id)
                .Join(_db.Groups, ug => ug.GroupId, g => g.Id, (ug, g) => g)
                .ToHashSetAsync();

            var roles = await _db.UserRoles
                .Where(ur => ur.UserId == user.Id)
                .Join(_db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => r)
                .ToHashSetAsync();

            var departments = await _db.DepartmentUsers
                .Where(du => du.UserId == user.Id)
                .Join(_db.Departments, du => du.DepartmentId, d => d.Id, (du, d) => d)
                .ToHashSetAsync();

            return new UserContext(user, groups, roles, new HashSet<string>(), departments);
        }

        public async Task<UserContext?> CreateByUsernameAsync(string username)
        {
            var user = await _db.Users
                .FirstOrDefaultAsync(u => u.Username == username);

            if (user is null) return null;

            return await CreateAsync(user);
        }
    }
}