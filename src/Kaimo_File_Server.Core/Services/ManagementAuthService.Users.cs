using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;

namespace Kaimo_File_Server.Core.Services;

/// <summary>
/// User-management checks of <see cref="ManagementAuthService"/>: scope (department membership),
/// the target-rank guard and the department-membership gate.
/// </summary>
public partial class ManagementAuthService
{
    // -- User Management --

    /// <summary>
    /// The target must lie in the actor's scope AND be outranked by the actor (see
    /// <see cref="OutranksAsync"/>), so a scoped admin can never act on a user holding more
    /// authority, e.g. reset the password of a global admin who is also a department member.
    /// Deleting additionally requires authority over every department the target belongs to.
    /// </summary>
    public async Task<bool> CanManageUserAsync(
        UserContext actor, Guid targetUserId, ManagementPermission required)
    {
        using var scope = BeginEvaluationScope();
        var inScope = (required & ManagementPermission.DeleteUsers) != 0
            ? await CoversAllDepartmentsOfUserAsync(actor, targetUserId, required)
            : await IsUserInScopeAsync(actor, targetUserId, required);

        return inScope && await OutranksAsync(actor, targetUserId);
    }

    public async Task<bool> CanAddUserToDepartmentAsync(
        UserContext actor, Guid userId, Guid departmentId)
    {
        using var scope = BeginEvaluationScope();
        if (!await CanManageDepartmentAsync(actor, departmentId, ManagementPermission.AssignDepartments))
            return false;

        // The user must already be within the actor's reach: otherwise adding them would pull a
        // foreign user (or a global admin) under the actor's user management.
        return await CanManageUserAsync(actor, userId, ManagementPermission.AssignDepartments);
    }

    public async Task<bool> IsPrincipalInScopeAsync(
        UserContext actor, Guid principalId, ManagementPermission required)
    {
        using var scope = BeginEvaluationScope();
        if (await _userRepo.GetByIdAsync(principalId) is not null)
            return await IsUserInScopeAsync(actor, principalId, required);

        // Groups of the Global department (incl. Everyone and Admins) need global authority.
        return await CanManageGroupAsync(actor, principalId, required);
    }

    public async Task<HashSet<Guid>?> GetUserIdsInScopeAsync(UserContext actor, ManagementPermission required)
    {
        // Same assignments and descendant rule as IsUserInScopeAsync; the department scope
        // already includes the descendants of every covered department.
        var departments = await GetAuthorizedDepartmentIdsAsync(actor, required);
        if (departments.IsUnrestricted)
            return null;

        var userIds = new HashSet<Guid>();
        foreach (var departmentId in departments.ScopeIds)
            userIds.UnionWith((await _departmentRepo.GetUsersAsync(departmentId) ?? []).Select(u => u.Id));
        return userIds;
    }

    // User ↔ Department is M:N, so membership is checked via the repo rather
    // than a stored DepartmentId. Only Department scope can grant user management.
    private Task<bool> IsUserInScopeAsync(
        UserContext actor, Guid targetUserId, ManagementPermission required)
        => AnyGrantAsync(actor, required, assignment =>
            assignment.ScopeType == ScopeType.Department
                ? _departmentRepo.IsUserInDepartmentOrDescendantAsync(targetUserId, assignment.ScopeId)
                : Task.FromResult(false));

    /// <summary>
    /// Every department of the target must be covered by an assignment holding
    /// <paramref name="required"/>. A target without an explicit department belongs to the
    /// Global department only, so it is covered by Global authority alone.
    /// </summary>
    private async Task<bool> CoversAllDepartmentsOfUserAsync(
        UserContext actor, Guid targetUserId, ManagementPermission required)
    {
        if (await HasGlobalPermissionAsync(actor, required))
            return true;

        var departments = await _departmentRepo.GetDepartmentsForUserAsync(targetUserId) ?? [];
        if (departments.Count == 0)
            return false;

        foreach (var department in departments)
        {
            if (!await CanManageDepartmentAsync(actor, department.Id, required))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Whether the actor covers every role assignment the target holds (directly or through a
    /// group): at each assignment's scope the actor's own effective permissions must be a
    /// superset of the role's bits. Same subset rule as <see cref="CanAssignRoleAsync"/>, without
    /// the delegation right. A target without assignments is always outranked.
    /// </summary>
    private async Task<bool> OutranksAsync(UserContext actor, Guid targetUserId)
    {
        var targetAssignments = await GetTargetAssignmentsAsync(targetUserId);
        if (targetAssignments.Count == 0)
            return true;

        var actorAssignments = await GetEffectiveAssignmentsAsync(actor);
        foreach (var assignment in targetAssignments)
        {
            var role = await GetRoleAsync(assignment.RoleId);
            if (role is null || role.ManagementPermissions == ManagementPermission.None)
                continue;

            var effective = await EffectivePermissionsAtAsync(
                actorAssignments, assignment.ScopeType, assignment.ScopeId);
            if ((role.ManagementPermissions & ~effective) != ManagementPermission.None)
                return false;
        }
        return true;
    }

    public Task<bool> CanCreateUserInDepartmentAsync(
        UserContext actor, Guid departmentId)
        => AnyGrantAsync(actor, ManagementPermission.CreateUsers, assignment =>
            assignment.ScopeType == ScopeType.Department
                ? IsDepartmentInScopeAsync(departmentId, assignment.ScopeId)
                : Task.FromResult(false));
}
