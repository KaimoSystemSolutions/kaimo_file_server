using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;

namespace Kaimo_File_Server.Core.Services;

/// <summary>
/// Core implementation of scoped management authorization
/// with department hierarchy support.
///
/// Decision logic:
///   1. Collect all ScopedRoleAssignments for the actor (user ID + group IDs)
///   2. For each assignment, load the Role and check ManagementPermission flags
///   3. Check if the target falls within the assignment's scope:
///      - Global → always matches
///      - Department → target must belong to the same department
///        OR any descendant (hierarchy inheritance)
///      - Share → target share ID must match exactly
///
/// Hierarchy inheritance:
///   An admin scoped to "Engineering" can also manage users/groups/shares
///   in "Engineering → Backend" and "Engineering → Frontend" (descendants).
///   This is controlled by the DepartmentRepository's hierarchy methods.
///
/// Deny-by-default: if no matching assignment grants the permission, access is denied.
/// No explicit deny entries — absence of a grant = no access.
/// </summary>
public class ManagementAuthService : IManagementAuthService
{
    private readonly IScopedRoleAssignmentRepository _assignmentRepo;
    private readonly IDepartmentRepository _departmentRepo;
    private readonly IRoleRepository _roleRepo;

    public ManagementAuthService(
        IScopedRoleAssignmentRepository assignmentRepo,
        IDepartmentRepository departmentRepo,
        IRoleRepository roleRepo)
    {
        _assignmentRepo = assignmentRepo;
        _departmentRepo = departmentRepo;
        _roleRepo = roleRepo;
    }

    // ── User Management ──

    public async Task<bool> CanManageUserAsync(
        UserContext actor, Guid targetUserId, ManagementPermission required)
    {
        var assignments = await GetEffectiveAssignmentsAsync(actor);

        foreach (var (assignment, role) in assignments)
        {
            if (!HasPermission(role, required))
                continue;

            switch (assignment.ScopeType)
            {
                case ScopeType.Global:
                    return true;

                case ScopeType.Department:
                    // Check direct membership AND descendant departments
                    if (await _departmentRepo.IsUserInDepartmentOrDescendantAsync(
                            targetUserId, assignment.ScopeId))
                        return true;
                    break;
            }
        }

        return false;
    }

    public async Task<bool> CanCreateUserInDepartmentAsync(
        UserContext actor, Guid departmentId)
    {
        var assignments = await GetEffectiveAssignmentsAsync(actor);

        foreach (var (assignment, role) in assignments)
        {
            if (!HasPermission(role, ManagementPermission.CreateUsers))
                continue;

            if (assignment.ScopeType == ScopeType.Global)
                return true;

            if (assignment.ScopeType == ScopeType.Department)
            {
                // Exact match or target department is a descendant
                if (assignment.ScopeId == departmentId)
                    return true;

                var descendants = await _departmentRepo.GetDescendantIdsAsync(assignment.ScopeId);
                if (descendants.Contains(departmentId))
                    return true;
            }
        }

        return false;
    }

    // ── Group Management ──

    public async Task<bool> CanManageGroupAsync(
        UserContext actor, Guid groupId, ManagementPermission required)
    {
        var assignments = await GetEffectiveAssignmentsAsync(actor);

        foreach (var (assignment, role) in assignments)
        {
            if (!HasPermission(role, required))
                continue;

            switch (assignment.ScopeType)
            {
                case ScopeType.Global:
                    return true;

                case ScopeType.Department:
                    // Check direct membership AND descendant departments
                    if (await _departmentRepo.IsGroupInDepartmentOrDescendantAsync(
                            groupId, assignment.ScopeId))
                        return true;
                    break;
            }
        }

        return false;
    }

    // ── Share Management ──

    public async Task<bool> CanManageShareAsync(
        UserContext actor, Guid shareId, ManagementPermission required)
    {
        var assignments = await GetEffectiveAssignmentsAsync(actor);

        foreach (var (assignment, role) in assignments)
        {
            if (!HasPermission(role, required))
                continue;

            switch (assignment.ScopeType)
            {
                case ScopeType.Global:
                    return true;

                case ScopeType.Department:
                    // Check direct membership AND descendant departments
                    if (await _departmentRepo.IsShareInDepartmentOrDescendantAsync(
                            shareId, assignment.ScopeId))
                        return true;
                    break;

                case ScopeType.Share:
                    if (assignment.ScopeId == shareId)
                        return true;
                    break;
            }
        }

        return false;
    }

    // ── Department Management ──

    public async Task<bool> CanManageDepartmentAsync(
        UserContext actor, Guid departmentId, ManagementPermission required)
    {
        var assignments = await GetEffectiveAssignmentsAsync(actor);

        foreach (var (assignment, role) in assignments)
        {
            if (!HasPermission(role, required))
                continue;

            if (assignment.ScopeType == ScopeType.Global)
                return true;

            if (assignment.ScopeType == ScopeType.Department)
            {
                // Exact match
                if (assignment.ScopeId == departmentId)
                    return true;

                // Target is a descendant of the scoped department
                var descendants = await _departmentRepo.GetDescendantIdsAsync(assignment.ScopeId);
                if (descendants.Contains(departmentId))
                    return true;
            }
        }

        return false;
    }

    // ── Generic Checks ──

    public async Task<bool> HasAnyPermissionAsync(
        UserContext actor, ManagementPermission required)
    {
        var assignments = await GetEffectiveAssignmentsAsync(actor);
        return assignments.Any(a => HasPermission(a.Role, required));
    }

    public async Task<AuthorizedScopeResult> GetAuthorizedDepartmentIdsAsync(
        UserContext actor, ManagementPermission required)
    {
        var assignments = await GetEffectiveAssignmentsAsync(actor);
        var result = new HashSet<Guid>();

        foreach (var (assignment, role) in assignments)
        {
            if (!HasPermission(role, required))
                continue;

            if (assignment.ScopeType == ScopeType.Global)
                return AuthorizedScopeResult.Unrestricted();

            if (assignment.ScopeType == ScopeType.Department)
            {
                // Include the scoped department itself
                result.Add(assignment.ScopeId);

                // Include all descendants (hierarchy inheritance)
                var descendants = await _departmentRepo.GetDescendantIdsAsync(assignment.ScopeId);
                foreach (var id in descendants)
                    result.Add(id);
            }
        }

        return result.Count > 0
            ? AuthorizedScopeResult.LimitedTo(result.ToList())
            : AuthorizedScopeResult.None();
    }

    public async Task<AuthorizedScopeResult> GetAuthorizedShareIdsAsync(
        UserContext actor, ManagementPermission required)
    {
        var assignments = await GetEffectiveAssignmentsAsync(actor);
        var result = new HashSet<Guid>();

        foreach (var (assignment, role) in assignments)
        {
            if (!HasPermission(role, required))
                continue;

            if (assignment.ScopeType == ScopeType.Global)
                return AuthorizedScopeResult.Unrestricted();

            if (assignment.ScopeType == ScopeType.Share)
            {
                result.Add(assignment.ScopeId);
            }

            if (assignment.ScopeType == ScopeType.Department)
            {
                // Shares from the scoped department
                var shares = await _departmentRepo.GetSharesAsync(assignment.ScopeId);
                foreach (var s in shares) result.Add(s.Id);

                // Shares from descendant departments
                var descendants = await _departmentRepo.GetDescendantIdsAsync(assignment.ScopeId);
                foreach (var descId in descendants)
                {
                    var descShares = await _departmentRepo.GetSharesAsync(descId);
                    foreach (var s in descShares) result.Add(s.Id);
                }
            }
        }

        return result.Count > 0
            ? AuthorizedScopeResult.LimitedTo(result.ToList())
            : AuthorizedScopeResult.None();
    }

    // ── Internals ──

    private static bool HasPermission(Role role, ManagementPermission required)
    {
        return (role.ManagementPermissions & required) == required;
    }

    private async Task<List<(ScopedRoleAssignment Assignment, Role Role)>>
        GetEffectiveAssignmentsAsync(UserContext actor)
    {
        var groupIds = actor.Groups?.Select(g => g.Id) ?? Enumerable.Empty<Guid>();
        var assignments = await _assignmentRepo.GetEffectiveAssignmentsAsync(
            actor.User.Id, groupIds);

        var result = new List<(ScopedRoleAssignment, Role)>();

        foreach (var assignment in assignments)
        {
            var role = await _roleRepo.GetByIdAsync(assignment.RoleId);
            if (role != null)
                result.Add((assignment, role));
        }

        return result;
    }
}