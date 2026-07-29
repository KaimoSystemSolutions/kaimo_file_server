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
        private readonly ApplicationDbContext _db;

        public UserRepository(ApplicationDbContext db)
        {
            _db = db;
        }

        // --------------------------------------------
        //  CRUD
        // --------------------------------------------

        /// <inheritdoc />
        public async Task<User?> GetByIdAsync(Guid id)
            => await _db.Users.FindAsync(id);

        /// <inheritdoc />
        public async Task<User?> GetByUsernameAsync(string username)
            => await _db.Users.FirstOrDefaultAsync(u => u.Username == username);

        /// <inheritdoc />
        public async Task<IEnumerable<User>> GetAllAsync()
            => await _db.Users.ToListAsync();

        /// <inheritdoc />
        public async Task<IReadOnlyList<SambaCredentialSource>>
            GetSambaCredentialBatchAsync(
                int offset,
                int count,
                CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

            return await _db.Users
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
            _db.Users.Add(user);
            await _db.SaveChangesAsync();
            return user;
        }

        /// <inheritdoc />
        public async Task UpdateAsync(User user)
        {
            _db.Users.Update(user);
            await _db.SaveChangesAsync();
        }

        /// <inheritdoc />
        public async Task DeleteAsync(Guid id)
        {
            var user = await _db.Users.FindAsync(id);
            if (user != null)
            {
                _db.Users.Remove(user);
                await _db.SaveChangesAsync();
            }
        }

        // --------------------------------------------
        //  Group / Role membership
        // --------------------------------------------

        /// <inheritdoc />
        public async Task<List<Group>> GetGroupsForUserAsync(Guid userId)
        {
            return await _db.UserGroups
                .Where(ug => ug.UserId == userId)
                .Join(_db.Groups, ug => ug.GroupId, g => g.Id, (_, g) => g)
                .OrderBy(g => g.Name)
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<List<Role>> GetRolesForUserAsync(Guid userId)
        {
            // A user's "direct" roles are its own GLOBAL-scoped role assignments.
            return await _db.ScopedRoleAssignments
                .Where(a => a.PrincipalId == userId && a.ScopeType == ScopeType.Global)
                .Join(_db.Roles, a => a.RoleId, r => r.Id, (_, r) => r)
                .Distinct()
                .OrderBy(r => r.Name)
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task SetGroupsForUserAsync(Guid userId, List<Guid> groupIds)
        {
            var existing = _db.UserGroups.Where(ug => ug.UserId == userId);
            _db.UserGroups.RemoveRange(existing);
            _db.UserGroups.AddRange(groupIds.Select(gId => new UserGroup(userId, gId)));
            await _db.SaveChangesAsync();
        }

        /// <inheritdoc />
        public async Task SetRolesForUserAsync(Guid userId, List<Guid> roleIds)
        {
            // Replaces only the user's OWN global-scoped assignments. Scoped
            // (department/share) assignments and group-inherited roles are left
            // untouched — they are managed through the scoped-assignment UI.
            var existing = _db.ScopedRoleAssignments
                .Where(a => a.PrincipalId == userId && a.ScopeType == ScopeType.Global);
            _db.ScopedRoleAssignments.RemoveRange(existing);
            _db.ScopedRoleAssignments.AddRange(
                roleIds.Select(rId => ScopedRoleAssignment.Global(userId, rId)));
            await _db.SaveChangesAsync();
        }

        // --------------------------------------------
        //  Targeted property updates
        // --------------------------------------------

        /// <inheritdoc />
        public async Task UpdateNameAsync(Guid userId, string newName)
        {
            await _db.Users
                .Where(u => u.Id == userId)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.Name, newName));
        }

        /// <inheritdoc />
        public async Task UpdatePasswordAsync(Guid userId, string passwordHash, string ntHash)
        {
            await _db.Users
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
            var user = await _db.Users.FindAsync(userId)
                ?? throw new KeyNotFoundException($"User {userId} not found");

            user.Description = description;
            user.Email = email;
            user.IsEnabled = isEnabled;
            user.CanChangePassword = canChangePassword;

            await _db.SaveChangesAsync();
        }
    }
}
