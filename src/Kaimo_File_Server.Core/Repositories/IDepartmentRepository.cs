using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;

namespace Kaimo_File_Server.Core.Repositories
{
    public interface IDepartmentRepository
    {
        // ── CRUD ──
        Task<Department?> GetByIdAsync(Guid id);
        Task<Department?> GetByNameAsync(string name);
        Task<List<Department>> GetAllAsync();
        Task<Department> CreateAsync(Department department);
        Task UpdateAsync(Department department);
        Task DeleteAsync(Guid id);

        // ── Hierarchy ──

        /// <summary>
        /// Returns the direct children of a department.
        /// </summary>
        Task<List<Department>> GetChildrenAsync(Guid parentId);

        /// <summary>
        /// Returns the ancestor chain from the given department up to the root.
        /// Ordered from immediate parent → root.
        /// Used for default permission inheritance (walk up until non-null found).
        /// </summary>
        Task<List<Department>> GetAncestorChainAsync(Guid departmentId);

        /// <summary>
        /// Returns all descendant department IDs (children, grandchildren, etc.)
        /// recursively. Used for admin scope inheritance:
        /// an admin scoped to Department X can also manage descendants of X.
        /// </summary>
        Task<HashSet<Guid>> GetDescendantIdsAsync(Guid departmentId);

        /// <summary>
        /// Returns all descendant departments as full entities.
        /// </summary>
        Task<List<Department>> GetDescendantsAsync(Guid departmentId);

        // ── User ↔ Department ──
        Task<List<User>> GetUsersAsync(Guid departmentId);
        Task<List<Department>> GetDepartmentsForUserAsync(Guid userId);
        Task AddUserAsync(Guid departmentId, Guid userId);
        Task RemoveUserAsync(Guid departmentId, Guid userId);
        Task<bool> IsUserInDepartmentAsync(Guid userId, Guid departmentId);

        /// <summary>
        /// Checks if a user is in the given department OR any of its descendants.
        /// Used by ManagementAuthService for hierarchy-aware scope checks.
        /// </summary>
        Task<bool> IsUserInDepartmentOrDescendantAsync(Guid userId, Guid departmentId);

        // ── Group ↔ Department ──
        Task<List<Group>> GetGroupsAsync(Guid departmentId);
        Task<List<Department>> GetDepartmentsForGroupAsync(Guid groupId);
        Task AddGroupAsync(Guid departmentId, Guid groupId);
        Task RemoveGroupAsync(Guid departmentId, Guid groupId);
        Task<bool> IsGroupInDepartmentAsync(Guid groupId, Guid departmentId);

        /// <summary>
        /// Checks if a group is in the given department OR any of its descendants.
        /// </summary>
        Task<bool> IsGroupInDepartmentOrDescendantAsync(Guid groupId, Guid departmentId);

        // ── Share ↔ Department ──
        Task<List<ShareDefinition>> GetSharesAsync(Guid departmentId);
        Task<List<Department>> GetDepartmentsForShareAsync(Guid shareId);
        Task AddShareAsync(Guid departmentId, Guid shareId);
        Task RemoveShareAsync(Guid departmentId, Guid shareId);
        Task<bool> IsShareInDepartmentAsync(Guid shareId, Guid departmentId);

        /// <summary>
        /// Checks if a share is in the given department OR any of its descendants.
        /// </summary>
        Task<bool> IsShareInDepartmentOrDescendantAsync(Guid shareId, Guid departmentId);
    }
}