using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;

namespace Kaimo_File_Server.Core.Services;

/// <summary>
/// Unified management authorization service.
///
/// TWO sources of authority, merged into one pipeline:
///
///   1. DIRECT role assignments (User → Role, via user_roles table)
///      → Treated as GLOBAL scope automatically.
///      → This is how system roles (Administrator, UserManager, ShareManager) work.
///      → Also works for custom roles assigned directly to a user.
///
///   2. SCOPED role assignments (ScopedRoleAssignment table)
///      → Explicitly scoped to Global, Department, or Share.
///      → This is how delegated administration works.
///      → Example: "Abteilungsleiter" role scoped to Department "Engineering"
///        → Can only manage users/groups within Engineering.
///
/// The merge happens in GetEffectiveAssignmentsAsync():
///   - Direct roles become synthetic Global-scoped assignments
///   - Scoped assignments come from the database
///   - Both are evaluated identically in every permission check
///
/// This eliminates all hardcoded IsInRole("Administrator") checks.
/// System roles are just roles with IsSystemRole=true and fixed ManagementPermission flags.
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
                if (assignment.ScopeId == departmentId)
                    return true;

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

    /// <summary>
    /// Checks if the actor has unrestricted (global) access for the given permission.
    /// Returns true only if the permission is granted at Global scope.
    /// </summary>
    public async Task<bool> HasGlobalPermissionAsync(
        UserContext actor, ManagementPermission required)
    {
        var assignments = await GetEffectiveAssignmentsAsync(actor);
        return assignments.Any(a =>
            HasPermission(a.Role, required) &&
            a.Assignment.ScopeType == ScopeType.Global);
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
                result.Add(assignment.ScopeId);

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
                var shares = await _departmentRepo.GetSharesAsync(assignment.ScopeId);
                foreach (var s in shares) result.Add(s.Id);

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

    /// <summary>
    /// Collects ALL effective assignments from two sources:
    ///
    /// 1. DIRECT role assignments (actor.Roles) → synthetic Global-scoped entries.
    ///    This is how system roles (Administrator, ShareManager, etc.) and
    ///    directly assigned custom roles contribute permissions.
    ///
    /// 2. SCOPED role assignments (ScopedRoleAssignment table) → from DB.
    ///    These carry explicit scope (Global / Department / Share).
    ///
    /// Both are returned in the same format so all permission checks
    /// evaluate them identically.
    /// </summary>
    private async Task<List<(ScopedRoleAssignment Assignment, Role Role)>>
        GetEffectiveAssignmentsAsync(UserContext actor)
    {
        var result = new List<(ScopedRoleAssignment, Role)>();

        // ── 1. Direct role assignments → Global scope ──
        foreach (var role in actor.Roles)
        {
            if (role.ManagementPermissions == ManagementPermission.None)
                continue;

            // Create a synthetic Global-scoped assignment
            // so the permission checks treat it identically
            var synthetic = new ScopedRoleAssignment(
                actor.User.Id, role.Id, ScopeType.Global, Guid.Empty);

            result.Add((synthetic, role));
        }

        // ── 2. Scoped assignments from DB ──
        var groupIds = actor.Groups?.Select(g => g.Id) ?? Enumerable.Empty<Guid>();
        var assignments = await _assignmentRepo.GetEffectiveAssignmentsAsync(
            actor.User.Id, groupIds);

        foreach (var assignment in assignments)
        {
            // Skip if we already have this role as Global (from direct assignment)
            // to avoid double-counting
            if (result.Any(r =>
                r.Role.Id == assignment.RoleId &&
                r.Assignment.ScopeType == ScopeType.Global))
                continue;

            var role = await _roleRepo.GetByIdAsync(assignment.RoleId);
            if (role != null)
                result.Add((assignment, role));
        }

        return result;
    }
}