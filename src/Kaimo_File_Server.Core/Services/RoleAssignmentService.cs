using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;

namespace Kaimo_File_Server.Core.Services;

/// <summary>
/// The single write path for scoped role assignments, shared by the user, role and
/// department pages so every page enforces the same rules.
///
/// "Global" has exactly one meaning: the Global department stands for the whole system.
/// An assignment made "at department Global" is therefore stored as
/// <see cref="ScopeType.Global"/> — the representation the authorization engine, role ACL
/// entries, the Admins-group mirror and the last-admin guard already understand.
/// </summary>
public sealed class RoleAssignmentService(
    IScopedRoleAssignmentRepository assignmentRepo,
    IRoleRepository roleRepo,
    IUserRepository userRepo,
    IGroupRepository groupRepo,
    IManagementAuthService mgmtAuth)
{
    /// <summary>A role that carries the complete <see cref="ManagementPermission.FullAdmin"/> mask.</summary>
    public static bool IsAdminRole(Role role)
        => (role.ManagementPermissions & ManagementPermission.FullAdmin) == ManagementPermission.FullAdmin;

    /// <summary>Maps "department Global" onto the system-wide scope; every other scope is kept.</summary>
    public static (ScopeType ScopeType, Guid ScopeId) Normalize(ScopeType scopeType, Guid scopeId)
        => scopeType == ScopeType.Global
           || (scopeType == ScopeType.Department && scopeId == WellKnownGUIDs.DEPARTMENT_GLOBAL)
            ? (ScopeType.Global, Guid.Empty)
            : (scopeType, scopeId);

    /// <summary>
    /// Creates the assignment after the no-privilege-elevation gate. Granting the admin role
    /// system-wide to a user also adds them to the Admins group (full mirror).
    /// </summary>
    /// <returns>A localized error message, or <c>null</c> on success.</returns>
    public async Task<string?> CreateAsync(
        UserContext actor, Guid principalId, Role role, ScopeType scopeType, Guid scopeId)
    {
        (scopeType, scopeId) = Normalize(scopeType, scopeId);

        if (!await mgmtAuth.CanAssignRoleAsync(actor, role.ManagementPermissions, scopeType, scopeId))
            return Resources.Web_Error_NoPermission;

        await assignmentRepo.CreateAsync(new ScopedRoleAssignment(principalId, role.Id, scopeType, scopeId));

        if (scopeType == ScopeType.Global && IsAdminRole(role)
            && await userRepo.GetByIdAsync(principalId) is not null)
            await ReconcileAdminCouplingAsync(principalId, true);

        return null;
    }

    /// <summary>
    /// Removes the assignment. Bounded by the same authority as creating it, and never
    /// removes the last source of an enabled system-wide administrator.
    /// </summary>
    /// <returns>A localized error message, or <c>null</c> on success.</returns>
    public async Task<string?> DeleteAsync(UserContext actor, ScopedRoleAssignment assignment)
    {
        var role = await roleRepo.GetByIdAsync(assignment.RoleId);
        var rolePerms = role?.ManagementPermissions ?? ManagementPermission.None;

        if (!await mgmtAuth.CanAssignRoleAsync(actor, rolePerms, assignment.ScopeType, assignment.ScopeId))
            return Resources.Web_Error_NoPermission;

        var revokesAdmin = assignment.ScopeType == ScopeType.Global && role is not null && IsAdminRole(role);
        if (revokesAdmin && (await GetEnabledGlobalAdminUserIdsAsync(assignment.Id)).Count == 0)
            return Resources.Web_Error_LastAdmin;

        await assignmentRepo.DeleteAsync(assignment.Id);

        if (revokesAdmin && await userRepo.GetByIdAsync(assignment.PrincipalId) is not null)
            await ReconcileAdminCouplingAsync(assignment.PrincipalId, false);

        return null;
    }

    /// <summary>
    /// Whether the actor may add members to the group. Joining a group confers every role the
    /// group holds (and joining Admins the system-wide admin role, via the mirror), so the actor
    /// must be allowed to assign each of them directly. Removing members needs no such check.
    /// </summary>
    public async Task<bool> CanAddGroupMembersAsync(UserContext actor, Guid groupId)
    {
        var grants = new List<(Guid RoleId, ScopeType ScopeType, Guid ScopeId)>();
        grants.AddRange((await assignmentRepo.GetByPrincipalAsync(groupId))
            .Select(a => (a.RoleId, a.ScopeType, a.ScopeId)));
        if (groupId == WellKnownGUIDs.GROUP_ADMINS)
            grants.Add((WellKnownGUIDs.ROLE_ADMIN, ScopeType.Global, Guid.Empty));

        foreach (var (roleId, scopeType, scopeId) in grants)
        {
            if (await roleRepo.GetByIdAsync(roleId) is { } role
                && !await mgmtAuth.CanAssignRoleAsync(actor, role.ManagementPermissions, scopeType, scopeId))
                return false;
        }
        return true;
    }

    /// <summary>
    /// The ENABLED users who effectively hold a system-wide admin role, directly or through a
    /// group. The last-admin guards never let this set become empty.
    /// </summary>
    public Task<HashSet<Guid>> GetEnabledGlobalAdminUserIdsAsync(Guid? excludeAssignmentId = null)
        => ManagementAuthService.GetGlobalPermissionHolderUserIdsAsync(
            assignmentRepo, roleRepo, userRepo, groupRepo, IsAdminRole, excludeAssignmentId);

    /// <summary>
    /// Keeps the two admin representations of a user in lockstep (full mirror): a direct
    /// system-wide <see cref="WellKnownGUIDs.ROLE_ADMIN"/> assignment and membership in the
    /// Admins group. Idempotent. Callers are responsible for the last-admin guard before revoking.
    /// </summary>
    public async Task ReconcileAdminCouplingAsync(Guid userId, bool shouldBeAdmin)
    {
        var globalAssignments = await assignmentRepo.GetByPrincipalAndScopeAsync(
            userId, ScopeType.Global, Guid.Empty);
        var adminAssignment = globalAssignments.FirstOrDefault(a => a.RoleId == WellKnownGUIDs.ROLE_ADMIN);

        if (shouldBeAdmin)
        {
            if (adminAssignment is null)
                await assignmentRepo.CreateAsync(ScopedRoleAssignment.Global(userId, WellKnownGUIDs.ROLE_ADMIN));
            await groupRepo.AddMemberAsync(WellKnownGUIDs.GROUP_ADMINS, userId);
        }
        else
        {
            if (adminAssignment is not null)
                await assignmentRepo.DeleteAsync(adminAssignment.Id);
            await groupRepo.RemoveMemberAsync(WellKnownGUIDs.GROUP_ADMINS, userId);
        }
    }
}
