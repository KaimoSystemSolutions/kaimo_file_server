using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Repositories
{
    /// <summary>
    /// EF Core implementation of <see cref="IUserRepository"/>.
    /// </summary>
    public class UserRepository : IUserRepository
    {
        private readonly IDbContextFactory<ApplicationDbContext> dbFactory;

        public UserRepository(IDbContextFactory<ApplicationDbContext> db)
        {
            dbFactory = db;
        }

        // --------------------------------------------
        //  CRUD
        // --------------------------------------------

        /// <inheritdoc />
        public async Task<User?> GetByIdAsync(Guid id)
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            return await db.Users.FindAsync(id);
        }

        /// <inheritdoc />
        public async Task<User?> GetByUsernameAsync(string username)
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            return await db.Users.FirstOrDefaultAsync(u => u.Username == username);

        }

        /// <inheritdoc />
        public async Task<IEnumerable<User>> GetAllAsync()
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            return await db.Users.ToListAsync();

        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<SambaCredentialSource>>
            GetSambaCredentialBatchAsync(
                int offset,
                int count,
                CancellationToken cancellationToken)
        {
            await using var db = await dbFactory.CreateDbContextAsync();

            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

            return await db.Users
                .AsNoTracking()
                .Where(user => user.IsEnabled)
                .OrderBy(user => user.Username)
                .ThenBy(user => user.Id)
                .Skip(offset)
                .Take(count)
                .Select(user => new SambaCredentialSource(
                    user.Username,
                    user.NtHash))
                .ToListAsync(cancellationToken);
        }

        /// <inheritdoc />
        public async Task<User> CreateAsync(User user)
        {
            await using var db = await dbFactory.CreateDbContextAsync();

            db.Users.Add(user);
            await db.SaveChangesAsync();
            return user;
        }

        /// <inheritdoc />
        public async Task UpdateAsync(User user)
        {
            await using var db = await dbFactory.CreateDbContextAsync();

            db.Users.Update(user);
            await db.SaveChangesAsync();
        }

        /// <inheritdoc />
        public async Task DeleteAsync(Guid id)
        {
            await using var db = await dbFactory.CreateDbContextAsync();

            var user = await db.Users.FindAsync(id);
            if (user != null)
            {
                db.Users.Remove(user);
                await db.SaveChangesAsync();
            }
        }

        // --------------------------------------------
        //  Group / Role membership
        // --------------------------------------------

        /// <inheritdoc />
        public async Task<List<Group>> GetGroupsForUserAsync(Guid userId)
        {
            await using var db = await dbFactory.CreateDbContextAsync();

            return await db.UserGroups
                .Where(ug => ug.UserId == userId)
                .Join(db.Groups, ug => ug.GroupId, g => g.Id, (_, g) => g)
                .OrderBy(g => g.Name)
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<List<Role>> GetRolesForUserAsync(Guid userId)
        {
            await using var db = await dbFactory.CreateDbContextAsync();

            // A user's "direct" roles are its own GLOBAL-scoped role assignments.
            return await db.ScopedRoleAssignments
                .Where(a => a.PrincipalId == userId && a.ScopeType == ScopeType.Global)
                .Join(db.Roles, a => a.RoleId, r => r.Id, (_, r) => r)
                .Distinct()
                .OrderBy(r => r.Name)
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task SetGroupsForUserAsync(Guid userId, List<Guid> groupIds)
        {
            await using var db = await dbFactory.CreateDbContextAsync();

            var existing = db.UserGroups.Where(ug => ug.UserId == userId);
            db.UserGroups.RemoveRange(existing);
            db.UserGroups.AddRange(groupIds.Select(gId => new UserGroup(userId, gId)));
            await db.SaveChangesAsync();
        }

        /// <inheritdoc />
        public async Task SetRolesForUserAsync(Guid userId, List<Guid> roleIds)
        {
            await using var db = await dbFactory.CreateDbContextAsync();

            // Replaces only the user's OWN global-scoped assignments. Scoped
            // (department/share) assignments and group-inherited roles are left
            // untouched — they are managed through the scoped-assignment UI.
            var existing = db.ScopedRoleAssignments
                .Where(a => a.PrincipalId == userId && a.ScopeType == ScopeType.Global);
            db.ScopedRoleAssignments.RemoveRange(existing);
            db.ScopedRoleAssignments.AddRange(
                roleIds.Select(rId => ScopedRoleAssignment.Global(userId, rId)));
            await db.SaveChangesAsync();
        }

        // --------------------------------------------
        //  Targeted property updates
        // --------------------------------------------

        /// <inheritdoc />
        public async Task UpdateNameAsync(Guid userId, string newName)
        {
            await using var db = await dbFactory.CreateDbContextAsync();

            await db.Users
                .Where(u => u.Id == userId)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.Name, newName));
        }

        /// <inheritdoc />
        public async Task UpdatePasswordAsync(Guid userId, string passwordHash, string ntHash)
        {
            await using var db = await dbFactory.CreateDbContextAsync();

            await db.Users
                .Where(u => u.Id == userId)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(x => x.PasswordHash, passwordHash)
                    .SetProperty(x => x.NtHash, ntHash));
        }

        /// <inheritdoc />
        public async Task UpdateProfileAsync(
            Guid userId, string description, string email,
            bool isEnabled, bool canChangePassword)
        {
            await using var db = await dbFactory.CreateDbContextAsync();

            var user = await db.Users.FindAsync(userId)
                ?? throw new KeyNotFoundException($"User {userId} not found");

            user.Description = description;
            user.Email = email;
            user.IsEnabled = isEnabled;
            user.CanChangePassword = canChangePassword;

            await db.SaveChangesAsync();
        }
    }
}
