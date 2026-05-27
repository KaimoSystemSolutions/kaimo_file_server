using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;

namespace Kaimo_File_Server.Core.Services
{
    /// <summary>
    /// Determines whether a user is allowed to perform administrative actions
    /// (managing users, groups, shares, ACLs) within their assigned scope.
    ///
    /// This is separate from IAclService which handles file-level permissions.
    /// This service handles management-level permissions:
    ///   "Can Marco create users in Department Entwicklung?"
    ///   "Can Anna manage ACLs on Share 'projekte'?"
    ///
    /// Authorization flow:
    ///   1. Collect all ScopedRoleAssignments for the actor (direct + via groups)
    ///   2. For each assignment, check if the Role has the required ManagementPermission
    ///   3. If yes, check if the target resource falls within the assignment's scope
    ///   4. Global scope always matches; Department scope checks membership; Share scope checks exact match
    /// </summary>
    public interface IManagementAuthService
    {
        // ── User management ──
        Task<bool> CanManageUserAsync(UserContext actor, Guid targetUserId, ManagementPermission required);
        Task<bool> CanCreateUserInDepartmentAsync(UserContext actor, Guid departmentId);

        // ── Group management ──
        Task<bool> CanManageGroupAsync(UserContext actor, Guid groupId, ManagementPermission required);

        // ── Share management ──
        Task<bool> CanManageShareAsync(UserContext actor, Guid shareId, ManagementPermission required);

        // ── Department management ──
        Task<bool> CanManageDepartmentAsync(UserContext actor, Guid departmentId, ManagementPermission required);

        // ── Generic check ──
        /// <summary>
        /// Checks if the actor has the given management permission in ANY scope.
        /// Used for UI: "should we show the 'Create User' button at all?"
        /// </summary>
        Task<bool> HasAnyPermissionAsync(UserContext actor, ManagementPermission required);

        /// <summary>
        /// Returns all departments where the actor has the given permission.
        /// Used for UI: "which departments can I manage users in?"
        /// </summary>
        Task<List<Guid>> GetAuthorizedDepartmentIdsAsync(UserContext actor, ManagementPermission required);

        /// <summary>
        /// Returns all share IDs where the actor has the given permission.
        /// </summary>
        Task<List<Guid>> GetAuthorizedShareIdsAsync(UserContext actor, ManagementPermission required);
    }
}