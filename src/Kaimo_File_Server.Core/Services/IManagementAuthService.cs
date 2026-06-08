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
    // ── User Management ──
    Task<bool> CanManageUserAsync(UserContext actor, Guid targetUserId, ManagementPermission required);
    Task<bool> CanCreateUserInDepartmentAsync(UserContext actor, Guid departmentId);

    // ── Group Management ──
    Task<bool> CanManageGroupAsync(UserContext actor, Guid groupId, ManagementPermission required);

    // ── Share Management ──
    Task<bool> CanManageShareAsync(UserContext actor, Guid shareId, ManagementPermission required);

    // ── Department Management ──
    Task<bool> CanManageDepartmentAsync(UserContext actor, Guid departmentId, ManagementPermission required);

    // ── Generic Checks ──

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
    /// Returns the set of department IDs the actor can manage for the given permission.
    /// Returns Unrestricted if the actor has Global scope.
    /// </summary>
    Task<AuthorizedScopeResult> GetAuthorizedDepartmentIdsAsync(UserContext actor, ManagementPermission required);

    /// <summary>
    /// Returns the set of share IDs the actor can manage for the given permission.
    /// Returns Unrestricted if the actor has Global scope.
    /// </summary>
    Task<AuthorizedScopeResult> GetAuthorizedShareIdsAsync(UserContext actor, ManagementPermission required);
}

/// <summary>
/// Result of a scope-limited authorization check.
/// Either unrestricted (Global admin) or limited to a set of entity IDs.
/// </summary>
public class AuthorizedScopeResult
{
    public bool IsUnrestricted { get; private init; }
    public IReadOnlyList<Guid> ScopeIds { get; private init; } = [];

    public static AuthorizedScopeResult Unrestricted()
        => new() { IsUnrestricted = true };

    public static AuthorizedScopeResult LimitedTo(List<Guid> ids)
        => new() { ScopeIds = ids };

    public static AuthorizedScopeResult None()
        => new() { ScopeIds = [] };
}