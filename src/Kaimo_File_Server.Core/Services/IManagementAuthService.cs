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
    ///   4. Global scope always matches
    ///   5. Department scope checks membership INCLUDING descendants (hierarchy inheritance)
    ///   6. Share scope checks exact match
    ///
    /// Hierarchy inheritance:
    ///   An admin scoped to Department X can also manage resources in all
    ///   descendant departments of X. This follows OOP inheritance semantics:
    ///   if Engineering → Backend → API Team, an admin scoped to Engineering
    ///   can manage users in Backend and API Team.
    /// </summary>
    public interface IManagementAuthService
    {
        // ── User management ──

        /// <summary>
        /// Checks if the actor can manage a specific user.
        /// Department scope includes descendant departments.
        /// </summary>
        Task<bool> CanManageUserAsync(UserContext actor, Guid targetUserId, ManagementPermission required);

        /// <summary>
        /// Checks if the actor can create users in a specific department.
        /// Also allows creation in descendant departments of the scoped department.
        /// </summary>
        Task<bool> CanCreateUserInDepartmentAsync(UserContext actor, Guid departmentId);

        // ── Group management ──

        /// <summary>
        /// Checks if the actor can manage a specific group.
        /// Department scope includes descendant departments.
        /// </summary>
        Task<bool> CanManageGroupAsync(UserContext actor, Guid groupId, ManagementPermission required);

        // ── Share management ──

        /// <summary>
        /// Checks if the actor can manage a specific share.
        /// Department scope includes shares in descendant departments.
        /// </summary>
        Task<bool> CanManageShareAsync(UserContext actor, Guid shareId, ManagementPermission required);

        // ── Department management ──

        /// <summary>
        /// Checks if the actor can manage a specific department.
        /// Also allows managing descendant departments of the scoped department.
        /// </summary>
        Task<bool> CanManageDepartmentAsync(UserContext actor, Guid departmentId, ManagementPermission required);

        // ── Generic check ──

        /// <summary>
        /// Checks if the actor has the given management permission in ANY scope.
        /// Used for UI: "should we show the 'Create User' button at all?"
        /// </summary>
        Task<bool> HasAnyPermissionAsync(UserContext actor, ManagementPermission required);

        /// <summary>
        /// Returns which departments the actor may manage with the given permission.
        /// Includes descendant departments in the result.
        ///
        /// Check <see cref="AuthorizedScopeResult.IsUnrestricted"/> first:
        ///   true  → global admin, no filtering needed
        ///   false → only the IDs in <see cref="AuthorizedScopeResult.ScopeIds"/>
        /// </summary>
        Task<AuthorizedScopeResult> GetAuthorizedDepartmentIdsAsync(
            UserContext actor, ManagementPermission required);

        /// <summary>
        /// Returns which shares the actor may manage with the given permission.
        /// Includes shares from descendant departments.
        ///
        /// Check <see cref="AuthorizedScopeResult.IsUnrestricted"/> first:
        ///   true  → global admin, no filtering needed
        ///   false → only the IDs in <see cref="AuthorizedScopeResult.ScopeIds"/>
        /// </summary>
        Task<AuthorizedScopeResult> GetAuthorizedShareIdsAsync(
            UserContext actor, ManagementPermission required);
    }
}