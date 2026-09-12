using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Repositories
{
    /// <summary>
    /// EF Core implementation of <see cref="IGroupRepository"/>.
    /// </summary>
    public class GroupRepository : IGroupRepository
    {
        private readonly IDbContextFactory<ApplicationDbContext> dbFactory;

        public GroupRepository(IDbContextFactory<ApplicationDbContext> db)
        {
            dbFactory = db;
        }

        /// <inheritdoc />
        public async Task<Group?> GetByIdAsync(Guid id)
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            return await db.Groups.FindAsync(id);
        }

        /// <inheritdoc />
        public async Task<IEnumerable<Group>> GetAllAsync()
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            return await db.Groups.ToListAsync();
        }
            
        /// <inheritdoc />
        public async Task<Group> CreateAsync(Group group)
        {
            await using var db = await dbFactory.CreateDbContextAsync();

            db.Groups.Add(group);
            await db.SaveChangesAsync();
            return group;
        }

        /// <inheritdoc />
        public async Task UpdateAsync(Group group)
        {
            await using var db = await dbFactory.CreateDbContextAsync();

            db.Groups.Update(group);
            await db.SaveChangesAsync();
        }

        /// <inheritdoc />
        public async Task DeleteAsync(Guid id)
        {
            // Root-cause guard: the two system groups can never be deleted, on any
            // write path (UI or future client-sync API). The ViewModel adds a nicer
            // localized message on top of this.
            if (WellKnownGUIDs.PROTECTED_GROUPS.Contains(id))
            {
                throw new InvalidOperationException(
                    $"Group {id} is a protected system group and cannot be deleted.");
            }

            await using var db = await dbFactory.CreateDbContextAsync();

            var group = await db.Groups.FindAsync(id);
            if (group == null)
            {
                return;
            }

            // No FK cascade reaches these: user_groups links by a plain Guid, and
            // PrincipalId is polymorphic user-or-group. Remove every link the group
            // held, or its id keeps showing up as a member / role- and grant-holder
            // after the group is gone.
            await db.UserGroups.Where(ug => ug.GroupId == id).ExecuteDeleteAsync();
            await db.ScopedRoleAssignments.Where(a => a.PrincipalId == id).ExecuteDeleteAsync();
            await db.CloudAccessGrants.Where(g => g.PrincipalId == id).ExecuteDeleteAsync();

            db.Groups.Remove(group);
            await db.SaveChangesAsync();
        }

        /// <inheritdoc />
        public async Task<List<User>> GetMembersAsync(Guid groupId)
        {
            await using var db = await dbFactory.CreateDbContextAsync();

            return await db.UserGroups
                .Where(ug => ug.GroupId == groupId)
                .Join(db.Users, ug => ug.UserId, u => u.Id, (_, u) => u)
                .OrderBy(u => u.Name)
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task SetMembersAsync(Guid groupId, List<Guid> userIds)
        {
            await using var db = await dbFactory.CreateDbContextAsync();

            var existing = db.UserGroups.Where(ug => ug.GroupId == groupId);
            db.UserGroups.RemoveRange(existing);
            db.UserGroups.AddRange(userIds.Select(uId => new UserGroup(uId, groupId)));
            await db.SaveChangesAsync();
        }

        /// <inheritdoc />
        public async Task AddMemberAsync(Guid groupId, Guid userId)
        {
            await using var db = await dbFactory.CreateDbContextAsync();

            var exists = await db.UserGroups
                .AnyAsync(ug => ug.GroupId == groupId && ug.UserId == userId);
            if (exists) return;

            db.UserGroups.Add(new UserGroup(userId, groupId));
            await db.SaveChangesAsync();
        }

        /// <inheritdoc />
        public async Task RemoveMemberAsync(Guid groupId, Guid userId)
        {
            await using var db = await dbFactory.CreateDbContextAsync();

            await db.UserGroups
                .Where(ug => ug.GroupId == groupId && ug.UserId == userId)
                .ExecuteDeleteAsync();
        }
    }
}