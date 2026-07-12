using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;

namespace Kaimo_File_Server.Core.Services;

/// <summary>
/// Unified management authorization service.
///
/// All permission checks go through this interface — no hardcoded role name checks.
/// System roles (Administrator, UserManager, ShareManager) are just roles with
/// IsSystemRole=true and fixed ManagementPermission flags.
///
/// TWO sources of authority:
///   1. Direct role assignments → treated as Global scope
///   2. ScopedRoleAssignment entries → explicitly scoped (Global/Department/Share)
/// </summary>
public interface IManagementAuthService
{
    // -- User Management --
    Task<bool> CanManageUserAsync(UserContext actor, Guid targetUserId, ManagementPermission required);
    Task<bool> CanCreateUserInDepartmentAsync(UserContext actor, Guid departmentId);

    // -- Group Management --
    Task<bool> CanManageGroupAsync(UserContext actor, Guid groupId, ManagementPermission required);

    // -- Share Management --
    Task<bool> CanManageShareAsync(UserContext actor, Guid shareId, ManagementPermission required);

    // -- Department Management --
    Task<bool> CanManageDepartmentAsync(UserContext actor, Guid departmentId, ManagementPermission required);

    // -- Generic Checks --

    /// <summary>
    /// Returns true if the actor has the required permission at ANY scope.
    /// Use for "can this user see the admin tab at all?" type checks.
    /// </summary>
    Task<bool> HasAnyPermissionAsync(UserContext actor, ManagementPermission required);

    /// <summary>
    /// Returns true if the actor has the required permission at GLOBAL scope.
    /// Use for "is this user an unrestricted admin?" type checks.
    /// A department-scoped admin will return false here.
    /// </summary>
    Task<bool> HasGlobalPermissionAsync(UserContext actor, ManagementPermission required);

    /// <summary>
    /// Aggregates the management-permission bits the actor effectively holds AT the
    /// given target scope, i.e. the union over every assignment whose scope <em>covers</em>
    /// the target (Global covers all; a department covers itself + descendants and their
    /// shares; a share covers only itself). This is the "what could this actor already do
    /// here?" set used to cap delegation.
    /// </summary>
    Task<ManagementPermission> GetEffectivePermissionsAtAsync(
        UserContext actor, ScopeType targetScopeType, Guid targetScopeId);

    /// <summary>
    /// Central no-privilege-elevation gate for role assignment. Returns true only when the
    /// actor holds <see cref="ManagementPermission.AssignRoles"/> at the target scope AND
    /// every permission bit carried by the role is one the actor itself effectively holds
    /// there. This enforces the documented "AssignRoles ⇒ only ≤ own permissions" invariant
    /// and prevents a narrowly-scoped delegate from granting broader (or Global) authority.
    /// </summary>
    Task<bool> CanAssignRoleAsync(
        UserContext actor, ManagementPermission rolePermissions,
        ScopeType targetScopeType, Guid targetScopeId);

    /// <summary>
    /// Returns the set of department IDs the actor can manage for the given permission.
    /// Returns Unrestricted if the actor has Global scope.
    /// Uses ALL-bits semantics: the role must hold every bit in <paramref name="required"/>.
    /// </summary>
    Task<AuthorizedScopeResult> GetAuthorizedDepartmentIdsAsync(UserContext actor, ManagementPermission required);

    /// <summary>
    /// Like <see cref="GetAuthorizedDepartmentIdsAsync"/> but with ANY-bit semantics:
    /// a department qualifies if the actor's role holds AT LEAST ONE of the bits in
    /// <paramref name="anyOf"/>. Pass a combined mask such as
    /// <see cref="ManagementPermission.GroupAdmin"/> to mean "any group-management right".
    /// Returns Unrestricted if the actor has Global scope.
    /// </summary>
    Task<AuthorizedScopeResult> GetAuthorizedDepartmentIdsAnyAsync(UserContext actor, ManagementPermission anyOf);

    /// <summary>
    /// Returns the set of share IDs the actor can manage for the given permission.
    /// Returns Unrestricted if the actor has Global scope.
    /// Uses ALL-bits semantics: the role must hold every bit in <paramref name="required"/>.
    /// </summary>
    Task<AuthorizedScopeResult> GetAuthorizedShareIdsAsync(UserContext actor, ManagementPermission required);

    /// <summary>
    /// Like <see cref="GetAuthorizedShareIdsAsync"/> but with ANY-bit semantics:
    /// the actor qualifies for a share if their role holds AT LEAST ONE of the bits
    /// in <paramref name="anyOf"/>. Pass a combined mask such as
    /// <see cref="ManagementPermission.ShareAdmin"/> to mean "any share-management right".
    /// Returns Unrestricted if the actor has Global scope.
    /// </summary>
    Task<AuthorizedScopeResult> GetAuthorizedShareIdsAnyAsync(UserContext actor, ManagementPermission anyOf);
}
