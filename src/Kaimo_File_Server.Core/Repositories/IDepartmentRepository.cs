using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;

namespace Kaimo_File_Server.Core.Repositories
{
    /// <summary>
    /// Repository for managing departments and their many-to-many relationships
    /// with users, groups, and shares.
    /// </summary>
    public interface IDepartmentRepository
    {
        // ────────────────────────────────────────────
        //  CRUD
        // ────────────────────────────────────────────

        /// <summary>
        /// Retrieves a department by its unique identifier.
        /// </summary>
        Task<Department?> GetByIdAsync(Guid id);

        /// <summary>
        /// Retrieves a department by its display name (case-insensitive match expected).
        /// </summary>
        Task<Department?> GetByNameAsync(string name);

        /// <summary>
        /// Returns every department in the system.
        /// </summary>
        Task<List<Department>> GetAllAsync();

        /// <summary>
        /// Returns the direct children of a parent department (one level deep).
        /// </summary>
        Task<List<Department>> GetChildrenAsync(Guid parentId);

        /// <summary>
        /// Creates a new department.
        /// </summary>
        /// <returns>The created department with server-generated fields populated.</returns>
        Task<Department> CreateAsync(Department department);

        /// <summary>
        /// Updates an existing department's properties (name, parent, etc.).
        /// </summary>
        Task UpdateAsync(Department department);

        /// <summary>
        /// Deletes a department by its identifier.
        /// Implementations should also clean up related join-table rows
        /// and scoped role assignments.
        /// </summary>
        Task DeleteAsync(Guid id);

        // ────────────────────────────────────────────
        //  User ↔ Department membership
        // ────────────────────────────────────────────

        /// <summary>Returns all users that belong to the specified department.</summary>
        Task<List<User>> GetUsersAsync(Guid departmentId);

        /// <summary>Returns all departments a user belongs to.</summary>
        Task<List<Department>> GetDepartmentsForUserAsync(Guid userId);

        /// <summary>Adds a user to a department.</summary>
        Task AddUserAsync(Guid departmentId, Guid userId);

        /// <summary>Removes a user from a department.</summary>
        Task RemoveUserAsync(Guid departmentId, Guid userId);

        /// <summary>Checks whether a user is a member of a department.</summary>
        Task<bool> IsUserInDepartmentAsync(Guid userId, Guid departmentId);

        // ────────────────────────────────────────────
        //  Group ↔ Department membership
        // ────────────────────────────────────────────

        /// <summary>Returns all groups associated with a department.</summary>
        Task<List<Group>> GetGroupsAsync(Guid departmentId);

        /// <summary>Returns all departments a group belongs to.</summary>
        Task<List<Department>> GetDepartmentsForGroupAsync(Guid groupId);

        /// <summary>Associates a group with a department.</summary>
        Task AddGroupAsync(Guid departmentId, Guid groupId);

        /// <summary>Removes a group from a department.</summary>
        Task RemoveGroupAsync(Guid departmentId, Guid groupId);

        /// <summary>Checks whether a group is associated with a department.</summary>
        Task<bool> IsGroupInDepartmentAsync(Guid groupId, Guid departmentId);

        // ────────────────────────────────────────────
        //  Share ↔ Department membership
        // ────────────────────────────────────────────

        /// <summary>Returns all shares linked to a department.</summary>
        Task<List<ShareDefinition>> GetSharesAsync(Guid departmentId);

        /// <summary>Returns all departments a share belongs to.</summary>
        Task<List<Department>> GetDepartmentsForShareAsync(Guid shareId);

        /// <summary>Links a share to a department.</summary>
        Task AddShareAsync(Guid departmentId, Guid shareId);

        /// <summary>Unlinks a share from a department.</summary>
        Task RemoveShareAsync(Guid departmentId, Guid shareId);

        /// <summary>Checks whether a share is linked to a department.</summary>
        Task<bool> IsShareInDepartmentAsync(Guid shareId, Guid departmentId);
    }
}