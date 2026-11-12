using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security.Management;

namespace Kaimo_File_Server.Core.Services.Management;

/// <summary>
/// Department-scoped roles are tied to membership: they are only assigned to members, and
/// leaving a department drops the user's ties to it (the memberships in its groups and the roles
/// assigned to the user at that department). Shared by the department, user and role pages.
/// </summary>
public sealed partial class RoleAssignmentService
{
    /// <summary>
    /// Whether a role may be assigned to the principal at the department: a user must be a member
    /// of exactly that department, a group must be one of its groups. Leaving the department
    /// revokes such roles (<see cref="RevokeDepartmentTiesAsync"/>), so a non-member's role could
    /// never be cleaned up.
    /// </summary>
    public async Task<bool> BelongsToDepartmentAsync(Guid principalId, Guid departmentId)
        => await groupRepo.GetByIdAsync(principalId) is { } group
            ? group.DepartmentId == departmentId
            : await departmentRepo.IsUserInDepartmentAsync(principalId, departmentId);

    /// <summary>The group memberships and department-scoped role assignments the user loses by
    /// leaving the department. System-wide assignments are never part of it.</summary>
    public async Task<DepartmentLeaveImpact> GetDepartmentLeaveImpactAsync(Guid userId, Guid departmentId)
    {
        var groups = (await userRepo.GetGroupsForUserAsync(userId))
            .Where(g => g.DepartmentId == departmentId).ToList();

        var roles = new List<(ScopedRoleAssignment, Role?)>();
        foreach (var assignment in await assignmentRepo.GetByPrincipalAndScopeAsync(
                     userId, ScopeType.Department, departmentId))
            roles.Add((assignment, await roleRepo.GetByIdAsync(assignment.RoleId)));

        return new DepartmentLeaveImpact(groups, roles);
    }

    /// <summary>
    /// Whether the actor may take the user out of the department's groups and revoke the user's
    /// roles at the department, which is what leaving the department does. Bounded per group like
    /// <see cref="CanRemoveGroupMembersAsync"/> and per role like <see cref="DeleteAsync"/>.
    /// </summary>
    public async Task<bool> CanLeaveDepartmentAsync(UserContext actor, Guid userId, Guid departmentId)
    {
        var impact = await GetDepartmentLeaveImpactAsync(userId, departmentId);
        foreach (var group in impact.Groups)
            if (!await CanRemoveGroupMembersAsync(actor, group.Id))
                return false;

        foreach (var (_, role) in impact.Roles)
            if (!await mgmtAuth.CanAssignRoleAsync(actor, role?.ManagementPermissions ?? ManagementPermission.None,
                    ScopeType.Department, departmentId))
                return false;

        return true;
    }

    /// <summary>
    /// Drops the user's memberships in the department's groups and deletes the user's role
    /// assignments at the department. Callers authorize with <see cref="CanLeaveDepartmentAsync"/>
    /// and remove the department membership itself. Returns what was removed.
    /// </summary>
    public async Task<DepartmentLeaveImpact> RevokeDepartmentTiesAsync(Guid userId, Guid departmentId)
    {
        var impact = await GetDepartmentLeaveImpactAsync(userId, departmentId);

        if (impact.Groups.Count > 0)
        {
            var dropped = impact.Groups.Select(g => g.Id).ToHashSet();
            var remaining = (await userRepo.GetGroupsForUserAsync(userId))
                .Where(g => !dropped.Contains(g.Id)).Select(g => g.Id).ToList();
            await userRepo.SetGroupsForUserAsync(userId, remaining);
        }

        foreach (var (assignment, _) in impact.Roles)
            await assignmentRepo.DeleteAsync(assignment.Id);

        return impact;
    }
}
