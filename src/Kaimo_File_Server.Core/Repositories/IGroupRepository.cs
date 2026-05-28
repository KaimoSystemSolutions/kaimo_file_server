using Kaimo_File_Server.Core.Domain.Identity;

namespace Kaimo_File_Server.Core.Repositories
{
    /// <summary>
    /// Repository for managing security groups and their user membership.
    /// </summary>
    public interface IGroupRepository
    {
        /// <summary>
        /// Retrieves a group by its unique identifier.
        /// </summary>
        Task<Group?> GetByIdAsync(Guid id);

        /// <summary>
        /// Returns every group in the system.
        /// </summary>
        Task<IEnumerable<Group>> GetAllAsync();

        /// <summary>
        /// Creates a new group.
        /// </summary>
        /// <returns>The created group with server-generated fields populated.</returns>
        Task<Group> CreateAsync(Group group);

        /// <summary>
        /// Deletes a group by its identifier.
        /// Implementations should cascade-delete related membership rows.
        /// </summary>
        Task DeleteAsync(Guid id);

        /// <summary>
        /// Returns all users that are members of the specified group.
        /// </summary>
        Task<List<User>> GetMembersAsync(Guid groupId);

        /// <summary>
        /// Replaces the full member list of a group with the given set of user IDs.
        /// Users not in <paramref name="userIds"/> are removed; new ones are added.
        /// </summary>
        Task SetMembersAsync(Guid groupId, List<Guid> userIds);
    }
}