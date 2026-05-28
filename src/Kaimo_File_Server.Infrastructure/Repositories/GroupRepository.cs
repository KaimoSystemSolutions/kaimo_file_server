using Kaimo_File_Server.Core.Domain.Identity;
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
        private readonly ApplicationDbContext _db;

        public GroupRepository(ApplicationDbContext db)
        {
            _db = db;
        }

        /// <inheritdoc />
        public async Task<Group?> GetByIdAsync(Guid id)
            => await _db.Groups.FindAsync(id);

        /// <inheritdoc />
        public async Task<IEnumerable<Group>> GetAllAsync()
            => await _db.Groups.ToListAsync();

        /// <inheritdoc />
        public async Task<Group> CreateAsync(Group group)
        {
            _db.Groups.Add(group);
            await _db.SaveChangesAsync();
            return group;
        }

        /// <inheritdoc />
        public async Task DeleteAsync(Guid id)
        {
            var group = await _db.Groups.FindAsync(id);
            if (group != null)
            {
                _db.Groups.Remove(group);
                await _db.SaveChangesAsync();
            }
        }

        /// <inheritdoc />
        public async Task<List<User>> GetMembersAsync(Guid groupId)
        {
            return await _db.UserGroups
                .Where(ug => ug.GroupId == groupId)
                .Join(_db.Users, ug => ug.UserId, u => u.Id, (_, u) => u)
                .OrderBy(u => u.Name)
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task SetMembersAsync(Guid groupId, List<Guid> userIds)
        {
            var existing = _db.UserGroups.Where(ug => ug.GroupId == groupId);
            _db.UserGroups.RemoveRange(existing);
            _db.UserGroups.AddRange(userIds.Select(uId => new UserGroup(uId, groupId)));
            await _db.SaveChangesAsync();
        }
    }
}