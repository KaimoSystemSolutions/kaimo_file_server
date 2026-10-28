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
public sealed partial class RoleAssignmentService(
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
    /// Creates the assignment after the no-privilege-elevation gate. The recipient must be within
    /// the actor's own <see cref="ManagementPermission.AssignRoles"/> reach (it may lie outside the
    /// target scope). Granting the admin role system-wide to a user also adds them to the Admins
    /// group (full mirror).
    /// </summary>
    /// <returns>A localized error message, or <c>null</c> on success.</returns>
    public async Task<string?> CreateAsync(
        UserContext actor, Guid principalId, Role role, ScopeType scopeType, Guid scopeId)
    {
        (scopeType, scopeId) = Normalize(scopeType, scopeId);

        if (!await mgmtAuth.IsPrincipalInScopeAsync(actor, principalId, ManagementPermission.AssignRoles))
            return Resources.Web_Error_NoPermission;

        if (!await mgmtAuth.CanAssignRoleAsync(actor, role.ManagementPermissions, scopeType, scopeId))
            return Resources.Web_Error_NoPermission;

        await assignmentRepo.CreateAsync(new ScopedRoleAssignment(principalId, role.Id, scopeType, scopeId));

        if (scopeType == ScopeType.Global && IsAdminRole(role)
            && await userRepo.GetByIdAsync(principalId) is not null)
            await ReconcileAdminCouplingAsync(principalId, true);

        return null;
    }

    /// <summary>
    /// The users and groups the actor may grant roles to: everyone for a global
    /// <see cref="ManagementPermission.AssignRoles"/> holder, otherwise the principals within the
    /// actor's department reach (see <see cref="IManagementAuthService.IsPrincipalInScopeAsync"/>).
    /// </summary>
    public async Task<(List<User> Users, List<Group> Groups)> GetAssignableRecipientsAsync(UserContext actor)
    {
        var users = (await userRepo.GetAllAsync()).ToList();
        var groups = (await groupRepo.GetAllAsync()).ToList();
        if (await mgmtAuth.HasGlobalPermissionAsync(actor, ManagementPermission.AssignRoles))
            return (users, groups);

        // Resolved once per covered department instead of one check per principal: a user is
        // reachable as a member of a covered department, a group by its department.
        using var evaluation = mgmtAuth.BeginEvaluationScope(); // read-only batch of checks
        var departments = await mgmtAuth.GetAuthorizedDepartmentIdsAsync(actor, ManagementPermission.AssignRoles)
                          ?? AuthorizedScopeResult.None();
        var userIds = await mgmtAuth.GetUserIdsInScopeAsync(actor, ManagementPermission.AssignRoles);
        if (departments.IsUnrestricted || userIds is null)
            return (users, groups);

        var departmentIds = departments.ScopeIds.ToHashSet();
        return (users.Where(u => userIds.Contains(u.Id)).ToList(),
                groups.Where(g => departmentIds.Contains(g.DepartmentId)).ToList());
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
    /// must be allowed to assign each of them directly.
    /// </summary>
    public Task<bool> CanAddGroupMembersAsync(UserContext actor, Guid groupId)
        => CanDelegateGroupGrantsAsync(actor, groupId);

    /// <summary>
    /// Whether the actor may remove members from the group. Leaving a group revokes every role
    /// the group holds, so — like <see cref="DeleteAsync"/> — it is bounded by the authority to
    /// assign each of them: a department admin cannot strip a role a global admin granted.
    /// </summary>
    public Task<bool> CanRemoveGroupMembersAsync(UserContext actor, Guid groupId)
        => CanDelegateGroupGrantsAsync(actor, groupId);

    /// <summary>
    /// Whether the actor may take the user out of every group of the department, which is what
    /// leaving the department does. Bounded per group like <see cref="CanRemoveGroupMembersAsync"/>.
    /// </summary>
    public async Task<bool> CanLeaveDepartmentGroupsAsync(UserContext actor, Guid userId, Guid departmentId)
    {
        foreach (var group in await userRepo.GetGroupsForUserAsync(userId))
        {
            if (group.DepartmentId == departmentId && !await CanRemoveGroupMembersAsync(actor, group.Id))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Whether every assignment at the scope stays within the actor's own effective permissions
    /// there, so deleting the scope (and its assignments with it) revokes nothing the actor could
    /// not hold itself — e.g. a right a global admin granted at a subdepartment.
    /// </summary>
    public async Task<bool> CoversAssignmentsAtAsync(UserContext actor, ScopeType scopeType, Guid scopeId)
    {
        var assignments = await assignmentRepo.GetByScopeAsync(scopeType, scopeId);
        if (assignments.Count == 0)
            return true;

        var effective = await mgmtAuth.GetEffectivePermissionsAtAsync(actor, scopeType, scopeId);
        foreach (var assignment in assignments)
        {
            if (await roleRepo.GetByIdAsync(assignment.RoleId) is { } role
                && (role.ManagementPermissions & ~effective) != ManagementPermission.None)
                return false;
        }
        return true;
    }

    private async Task<bool> CanDelegateGroupGrantsAsync(UserContext actor, Guid groupId)
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
