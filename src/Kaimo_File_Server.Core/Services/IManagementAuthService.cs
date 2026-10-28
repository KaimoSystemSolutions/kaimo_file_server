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
///
/// How roles combine:
///   • Permission checks (<c>CanManage*</c>, <see cref="HasGlobalPermissionAsync"/>, the
///     ALL-bits scope lists) are evaluated PER ROLE ASSIGNMENT: when the required mask has several
///     bits, a single role must carry all of them. Rights split across two roles do not add up
///     there; in particular "global admin" means one global role carrying the full
///     <see cref="ManagementPermission.FullAdmin"/> mask (the last-admin guards count the same way).
///   • <see cref="GetEffectivePermissionsAtAsync"/> and the delegation gate
///     (<see cref="CanAssignRoleAsync"/>, the target-rank check) take the UNION of every assignment
///     covering the scope, because they ask "which bits could the actor already use here?".
///   • Group memberships are read from the store on every check, not from the
///     <see cref="UserContext"/> snapshot (see <see cref="BeginEvaluationScope"/> for caching).
/// </summary>
public interface IManagementAuthService
{
    // -- User Management --

    /// <summary>
    /// Whether the actor may perform <paramref name="required"/> on the target user: the target
    /// is a member of a department in the actor's scope (for <see cref="ManagementPermission.DeleteUsers"/>:
    /// every department of the target is) AND the actor outranks the target, i.e. covers every
    /// role assignment the target holds.
    /// </summary>
    Task<bool> CanManageUserAsync(UserContext actor, Guid targetUserId, ManagementPermission required);
    Task<bool> CanCreateUserInDepartmentAsync(UserContext actor, Guid departmentId);

    /// <summary>
    /// Management rank only: whether the actor covers every role assignment the target holds
    /// (the rank part of <see cref="CanManageUserAsync"/>, without its department scope). A target
    /// without assignments is always outranked. Used where the actor acts on something that runs
    /// with the target's authority, e.g. a public link of another creator.
    /// </summary>
    Task<bool> OutranksUserAsync(UserContext actor, Guid targetUserId);

    /// <summary>
    /// Whether the actor may add the user to the department: requires
    /// <see cref="ManagementPermission.AssignDepartments"/> on the department and authority over
    /// the user (same rule as <see cref="CanManageUserAsync"/>), so a foreign user can never be
    /// pulled under the actor's user management.
    /// </summary>
    Task<bool> CanAddUserToDepartmentAsync(UserContext actor, Guid userId, Guid departmentId);

    /// <summary>
    /// Whether the principal (user or group) of a role assignment lies within the actor's reach
    /// for <paramref name="required"/>: a user who is a member of a department (or descendant)
    /// covered by such an assignment, or a group whose department is covered. Global authority
    /// reaches every principal; share-scoped authority reaches none. Unlike
    /// <see cref="CanManageUserAsync"/> there is no rank check: granting rights to a higher-ranked
    /// user elevates nobody.
    /// </summary>
    Task<bool> IsPrincipalInScopeAsync(UserContext actor, Guid principalId, ManagementPermission required);

    /// <summary>
    /// The users within the actor's reach for <paramref name="required"/>: members of a department
    /// (or descendant) covered by an assignment holding it. This is the scope part of
    /// <see cref="CanManageUserAsync"/> and <see cref="IsPrincipalInScopeAsync"/>, resolved once per
    /// covered department instead of once per user, so pickers can filter in memory. <c>null</c>
    /// means every user (global authority). No rank check.
    /// </summary>
    Task<HashSet<Guid>?> GetUserIdsInScopeAsync(UserContext actor, ManagementPermission required);

    // -- Group Management --
    Task<bool> CanManageGroupAsync(UserContext actor, Guid groupId, ManagementPermission required);

    // -- Share Management --
    Task<bool> CanManageShareAsync(UserContext actor, Guid shareId, ManagementPermission required);

    // -- Department Management --
    Task<bool> CanManageDepartmentAsync(UserContext actor, Guid departmentId, ManagementPermission required);

    // -- Evaluation scope --

    /// <summary>
    /// Opens a scope in which lookups (the actor's assignments and group memberships, roles, the
    /// department tree, shares) are cached until it is disposed; without one, every check reads
    /// the store. Use it only around a read-only batch of checks (e.g. filtering a picker), never
    /// across a mutation. Nested scopes reuse the outer cache.
    /// </summary>
    IDisposable BeginEvaluationScope();

    // -- Generic Checks --

    /// <summary>
    /// Returns true if the actor has the required permission at ANY scope.
    /// Use for "can this user see the admin tab at all?" type checks.
    /// </summary>
    Task<bool> HasAnyPermissionAsync(UserContext actor, ManagementPermission required);

    /// <summary>
    /// Returns true if the actor has the required permission at GLOBAL scope.
    /// Use for "is this user an unrestricted admin?" type checks.
    /// A department-scoped admin will return false here. ALL-bits per role: one global role must
    /// hold every bit in <paramref name="required"/> (see the class remarks).
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
