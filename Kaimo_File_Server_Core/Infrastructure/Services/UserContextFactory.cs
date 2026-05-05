using Kaimo_File_Server_Core.Core.Domain;
using Kaimo_File_Server_Core.Core.Domain.Identity;
using Kaimo_File_Server_Core.Core.Repositories;
using Kaimo_File_Server_Core.Core.Repositories.Kaimo_File_Server_Core.Core.Repositories;
using Kaimo_File_Server_Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server_Core.Infrastructure.Services
{
    public class UserContextFactory : IUserContextFactory
    {
        private readonly ApplicationDbContext _db;

        public UserContextFactory(ApplicationDbContext db)
        {
            _db = db;
        }

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

            var permissions = new HashSet<string>();

            return new UserContext(user, groups, roles, permissions);
        }
    }
}
