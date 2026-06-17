using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Services;

public class UserContextFactory : IUserContextFactory
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;

    public UserContextFactory(IDbContextFactory<ApplicationDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<UserContext> CreateAsync(User user)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var groups = await db.UserGroups
            .Where(ug => ug.UserId == user.Id)
            .Join(db.Groups, ug => ug.GroupId, g => g.Id, (ug, g) => g)
            .ToHashSetAsync();

        var roles = await db.UserRoles
            .Where(ur => ur.UserId == user.Id)
            .Join(db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => r)
            .ToHashSetAsync();

        var departments = await db.DepartmentUsers
            .Where(du => du.UserId == user.Id)
            .Join(db.Departments, du => du.DepartmentId, d => d.Id, (du, d) => d)
            .ToHashSetAsync();

        return new UserContext(user, groups, roles, new HashSet<string>(), departments);
    }

    public async Task<UserContext?> CreateByUsernameAsync(string username)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username);
        if (user is null) return null;
        return await CreateAsync(user);
    }

    public async Task<UserContext?> CreateByUserIdAsync(Guid userId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return null;
        return await CreateAsync(user);
    }
}