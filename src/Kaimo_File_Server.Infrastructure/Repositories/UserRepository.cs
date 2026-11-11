using System.Text.Json;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Domain.Notifications;
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
        public async Task<User?> GetByUsernameIgnoreCaseAsync(string username)
        {
            var name = username.Trim().ToLowerInvariant();
            await using var db = await dbFactory.CreateDbContextAsync();
            return await db.Users.FirstOrDefaultAsync(u => u.Username.ToLower() == name);
        }

        /// <inheritdoc />
        public async Task<IEnumerable<User>> GetAllAsync()
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            return await db.Users.ToListAsync();

        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<PrincipalSummary>> GetSummariesAsync()
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            return await db.Users.AsNoTracking()
                .Select(u => new PrincipalSummary(u.Id, u.Name, u.Username, u.Email))
                .ToListAsync();
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
                    user.NtHash,
                    user.SmbUnlockRequestedAtUtc))
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
        public Task DeleteAsync(Guid id)
            => dbFactory.ExecuteResilientAsync(db => DeleteCoreAsync(db, id));

        private static async Task DeleteCoreAsync(ApplicationDbContext db, Guid id)
        {
            // One transaction: a failure part-way must not leave a user whose
            // memberships are already gone, or orphaned links to a deleted user.
            await using var tx = await db.Database.BeginTransactionAsync();

            var user = await db.Users.FindAsync(id);
            if (user == null)
            {
                return;
            }

            // These link tables reference the user by a plain Guid (PrincipalId is
            // polymorphic user-or-group), so there is no FK cascade to rely on.
            // Remove every assignment the user held, or its id keeps showing up
            // under groups / roles / departments after the account is gone.
            await db.UserGroups.Where(ug => ug.UserId == id).ExecuteDeleteAsync();
            await db.DepartmentUsers.Where(du => du.UserId == id).ExecuteDeleteAsync();
            await db.ScopedRoleAssignments.Where(a => a.PrincipalId == id).ExecuteDeleteAsync();
            await db.CloudAccessGrants.Where(g => g.PrincipalId == id).ExecuteDeleteAsync();

            // File ACL entries and public links (which run under their creator's identity)
            // only ever served this account and can never apply to anyone else.
            await db.AccessEntries.Where(e => e.PrincipalId == id).ExecuteDeleteAsync();
            await db.ShareLinks.Where(l => l.CreatedByUserId == id).ExecuteDeleteAsync();

            // Syncs are shared configuration and stay. Their FKs to the user would block the
            // delete; without an execution user the scheduler skips them until an admin saves
            // the schedule again (which binds it to that admin).
            await db.SyncDefinitions.Where(d => d.RunAsUserId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.RunAsUserId, (Guid?)null));
            await db.SyncDefinitions.Where(d => d.CreatedByUserId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.CreatedByUserId, (Guid?)null));

            await PruneFromMailRulesAsync(db, id);
            await PruneFromSettingAsync<SecondFactorPolicy>(db, SecondFactorPolicy.ConfigKey,
                p => p.UserIds.Remove(id));
            await PruneFromSettingAsync<AccessRestrictionSettings>(db, AccessRestrictionSettings.ConfigKey,
                s => s.Rules.Aggregate(false, (changed, r) => r.UserIds.Remove(id) | changed));

            db.Users.Remove(user);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }

        private static async Task PruneFromMailRulesAsync(ApplicationDbContext db, Guid id)
        {
            var value = id.ToString();
            var rules = await db.MailRules.Where(r => r.RecipientsJson.Contains(value)).ToListAsync();
            foreach (var rule in rules)
                rule.SetRecipients(rule.GetRecipients()
                    .Where(r => !(r.Kind == RecipientKind.User && string.Equals(r.Value, value, StringComparison.OrdinalIgnoreCase))));
        }

        /// <summary>
        /// Removes the user from a JSON policy in <c>ConfigSettings</c>. Written through the
        /// tracked row (same default serializer as <c>ConfigRepository</c>); in-memory caches may
        /// hold the dead id until they expire, which is harmless since no account can reuse it.
        /// </summary>
        private static async Task PruneFromSettingAsync<T>(ApplicationDbContext db, string key, Func<T, bool> remove)
        {
            var setting = await db.ConfigSettings.FirstOrDefaultAsync(s => s.Key == key);
            if (setting is null) return;
            var value = JsonSerializer.Deserialize<T>(setting.Value);
            if (value is null || !remove(value)) return;
            setting.Value = JsonSerializer.Serialize(value);
            setting.UpdatedAt = DateTime.UtcNow;
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
        public Task UpdatePasswordAsync(Guid userId, string passwordHash, string ntHash, bool changedByUser = false)
            => dbFactory.ExecuteResilientAsync(
                db => UpdatePasswordCoreAsync(db, userId, passwordHash, ntHash, changedByUser));

        private static async Task UpdatePasswordCoreAsync(
            ApplicationDbContext db, Guid userId, string passwordHash, string ntHash, bool changedByUser)
        {
            await using var tx = await db.Database.BeginTransactionAsync();

            // A new security stamp invalidates every web and client-API JWT issued before
            // this change (the stamp is embedded in each token and compared on validation).
            var securityStamp = User.NewSecurityStamp();
            await db.Users
                .Where(u => u.Id == userId)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(x => x.PasswordHash, passwordHash)
                    .SetProperty(x => x.NtHash, ntHash)
                    .SetProperty(x => x.SecurityStamp, securityStamp)
                    .SetProperty(x => x.MustChangePassword, x => !changedByUser && x.MustChangePassword));

            // A password change/reset is the standard response to a compromise, so every
            // client-API refresh token of the account is revoked with it. Otherwise a stolen
            // refresh token would keep rotating indefinitely despite the new password.
            var now = DateTime.UtcNow;
            await db.RefreshTokens
                .Where(t => t.UserId == userId && t.RevokedAtUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAtUtc, now));

            await tx.CommitAsync();
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

        /// <inheritdoc />
        public async Task UpdatePersonalNamesAsync(Guid userId, string? firstName, string? lastName)
        {
            await using var db = await dbFactory.CreateDbContextAsync();

            var user = await db.Users.FindAsync(userId)
                ?? throw new KeyNotFoundException($"User {userId} not found");

            user.FirstName = firstName;
            user.LastName = lastName;

            await db.SaveChangesAsync();
        }

        /// <inheritdoc />
        public async Task UpdateHomeDirectoryEnabledAsync(Guid userId, bool enabled)
        {
            await using var db = await dbFactory.CreateDbContextAsync();

            var user = await db.Users.FindAsync(userId)
                ?? throw new KeyNotFoundException($"User {userId} not found");

            user.HomeDirectoryEnabled = enabled;

            await db.SaveChangesAsync();
        }

        /// <inheritdoc />
        public async Task UpdateServiceAccessAsync(Guid userId, UserServiceAccess access)
        {
            await using var db = await dbFactory.CreateDbContextAsync();

            var user = await db.Users.FindAsync(userId)
                ?? throw new KeyNotFoundException($"User {userId} not found");

            user.ServiceAccess = access;

            await db.SaveChangesAsync();
        }

        /// <inheritdoc />
        public async Task RequestSmbUnlockAsync(Guid userId, DateTime requestedAtUtc)
        {
            await using var db = await dbFactory.CreateDbContextAsync();

            var user = await db.Users.FindAsync(userId)
                ?? throw new KeyNotFoundException($"User {userId} not found");

            user.SmbUnlockRequestedAtUtc = requestedAtUtc;

            await db.SaveChangesAsync();
        }

        /// <inheritdoc />
        // SaveChanges path (see UpdatePersonalNamesAsync) so the read-only demo guard applies.
        public async Task UpdatePhotoAsync(Guid userId, byte[]? photo, string? contentType)
        {
            await using var db = await dbFactory.CreateDbContextAsync();

            var user = await db.Users.FindAsync(userId)
                ?? throw new KeyNotFoundException($"User {userId} not found");

            user.Photo = photo;
            user.PhotoContentType = contentType;

            await db.SaveChangesAsync();
        }
    }
}
