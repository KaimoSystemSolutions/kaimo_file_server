using Kaimo_File_Server.Core.Domain.Identity;

namespace Kaimo_File_Server.Core.Repositories
{
    /// <summary>
    /// Repository for managing roles and their user membership.
    /// </summary>
    public interface IRoleRepository
    {
        /// <summary>
        /// Retrieves a role by its unique identifier.
        /// </summary>
        Task<Role?> GetByIdAsync(Guid id);

        /// <summary>
        /// Retrieves a role by its display name (case-insensitive match expected).
        /// </summary>
        Task<Role?> GetByNameAsync(string name);

        /// <summary>
        /// Returns every role in the system.
        /// </summary>
        Task<IEnumerable<Role>> GetAllAsync();

        /// <summary>
        /// Creates a new role.
        /// </summary>
        /// <returns>The created role with server-generated fields populated.</returns>
        Task<Role> CreateAsync(Role role);

        /// <summary>
        /// Updates an existing role's properties (name, description, permissions, etc.).
        /// </summary>
        Task UpdateAsync(Role role);

        /// <summary>
        /// Deletes a role by its identifier.
        /// Implementations should cascade-delete related assignment rows.
        /// </summary>
        Task DeleteAsync(Guid id);
    }
}