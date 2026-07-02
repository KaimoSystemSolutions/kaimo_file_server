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
///
///   2. SCOPED role assignments (ScopedRoleAssignment table)
///      → Explicitly scoped to Global, Department, or Share.
///
/// Group and Share now carry a direct DepartmentId FK,
/// so scope checks load the entity and compare DepartmentId
/// against the scoped department (+ its descendants).
/// </summary>
public class ManagementAuthService : IManagementAuthService
{
    private readonly IScopedRoleAssignmentRepository _assignmentRepo;
    private readonly IDepartmentRepository _departmentRepo;
    private readonly IRoleRepository _roleRepo;
    private readonly IGroupRepository _groupRepo;
    private readonly IShareRepository _shareRepo;

    public ManagementAuthService(
        IScopedRoleAssignmentRepository assignmentRepo,
        IDepartmentRepository departmentRepo,
        IRoleRepository roleRepo,
        IGroupRepository groupRepo,
        IShareRepository shareRepo)
    {
        _assignmentRepo = assignmentRepo;
        _departmentRepo = departmentRepo;
        _roleRepo = roleRepo;
        _groupRepo = groupRepo;
        _shareRepo = shareRepo;
    }

    // -- User Management --

    public Task<bool> CanManageUserAsync(
        UserContext actor, Guid targetUserId, ManagementPermission required)
        // User ↔ Department is M:N, so membership is checked via the repo rather
        // than a stored DepartmentId. Only Department scope can grant user management.
        => AnyGrantAsync(actor, required, assignment =>
            assignment.ScopeType == ScopeType.Department
                ? _departmentRepo.IsUserInDepartmentOrDescendantAsync(targetUserId, assignment.ScopeId)
                : Task.FromResult(false));

    public Task<bool> CanCreateUserInDepartmentAsync(
        UserContext actor, Guid departmentId)
        => AnyGrantAsync(actor, ManagementPermission.CreateUsers, assignment =>
            assignment.ScopeType == ScopeType.Department
                ? IsDepartmentInScopeAsync(departmentId, assignment.ScopeId)
                : Task.FromResult(false));

    // -- Group Management --

    public async Task<bool> CanManageGroupAsync(
        UserContext actor, Guid groupId, ManagementPermission required)
    {
        // Load group once to read its DepartmentId (direct FK)
        var group = await _groupRepo.GetByIdAsync(groupId);
        if (group == null) return false;

        return await AnyGrantAsync(actor, required, assignment =>
            assignment.ScopeType == ScopeType.Department
                ? IsDepartmentInScopeAsync(group.DepartmentId, assignment.ScopeId)
                : Task.FromResult(false));
    }

    // -- Share Management --

    public async Task<bool> CanManageShareAsync(
        UserContext actor, Guid shareId, ManagementPermission required)
    {
        // Load share once to read its DepartmentId (direct FK)
        var share = await _shareRepo.GetByIdAsync(shareId);
        if (share == null) return false;

        return await AnyGrantAsync(actor, required, assignment => assignment.ScopeType switch
        {
            ScopeType.Department => IsDepartmentInScopeAsync(share.DepartmentId, assignment.ScopeId),
            ScopeType.Share => Task.FromResult(assignment.ScopeId == shareId),
            _ => Task.FromResult(false)
        });
    }

    // -- Department Management --

    public Task<bool> CanManageDepartmentAsync(
        UserContext actor, Guid departmentId, ManagementPermission required)
        => AnyGrantAsync(actor, required, assignment =>
            assignment.ScopeType == ScopeType.Department
                ? IsDepartmentInScopeAsync(departmentId, assignment.ScopeId)
                : Task.FromResult(false));

    // -- Generic Checks --

    public async Task<bool> HasAnyPermissionAsync(
        UserContext actor, ManagementPermission required)
    {
        var assignments = await GetEffectiveAssignmentsAsync(actor);
        // "Any" semantics: a single overlapping bit is enough. When the caller
        // passes a combined flag (e.g. CreateGroups | ManageGroupMembers), the
        // actor only needs ONE of those permissions — not all of them.
        return assignments.Any(a => HasAnyOverlap(a.Role, required));
    }

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

    public Task<AuthorizedScopeResult> GetAuthorizedShareIdsAsync(
        UserContext actor, ManagementPermission required)
        => GetAuthorizedShareIdsCoreAsync(actor, role => HasPermission(role, required));

    public Task<AuthorizedScopeResult> GetAuthorizedShareIdsAnyAsync(
        UserContext actor, ManagementPermission anyOf)
        => GetAuthorizedShareIdsCoreAsync(actor, role => HasAnyOverlap(role, anyOf));

    /// <summary>
    /// Shared scope-resolution for shares. <paramref name="roleMatches"/> decides
    /// whether a role qualifies (ALL-bits vs ANY-bit), the scope walk is identical.
    /// </summary>
    private async Task<AuthorizedScopeResult> GetAuthorizedShareIdsCoreAsync(
        UserContext actor, Func<Role, bool> roleMatches)
    {
        var assignments = await GetEffectiveAssignmentsAsync(actor);
        var result = new HashSet<Guid>();

        foreach (var (assignment, role) in assignments)
        {
            if (!roleMatches(role))
                continue;

            if (assignment.ScopeType == ScopeType.Global)
                return AuthorizedScopeResult.Unrestricted();

            if (assignment.ScopeType == ScopeType.Share)
            {
                result.Add(assignment.ScopeId);
            }

            if (assignment.ScopeType == ScopeType.Department)
            {
                // Get shares directly belonging to this department
                var shares = await _departmentRepo.GetSharesAsync(assignment.ScopeId);
                foreach (var s in shares) result.Add(s.Id);

                // And shares in descendant departments
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

    // -- Internals --

    /// <summary>
    /// ALL-bits check: the role must hold every bit in <paramref name="required"/>.
    /// Used where a combined flag expresses a conjunction, e.g.
    /// <see cref="ManagementPermission.FullAdmin"/> for the global-admin check.
    /// </summary>
    private static bool HasPermission(Role role, ManagementPermission required)
    {
        return (role.ManagementPermissions & required) == required;
    }

    /// <summary>
    /// ANY-bit check: the role holds at least one bit of <paramref name="required"/>.
    /// Used for "can the actor do any of these things" gates, where a combined
    /// flag expresses a disjunction (e.g. CreateGroups | ManageGroupMembers).
    /// For single-bit arguments this is identical to <see cref="HasPermission"/>.
    /// </summary>
    private static bool HasAnyOverlap(Role role, ManagementPermission required)
    {
        return (role.ManagementPermissions & required) != 0;
    }

    /// <summary>
    /// Shared evaluation for all "can the actor do X here?" checks. Walks the actor's
    /// effective assignments, keeps only those whose role holds every bit of
    /// <paramref name="required"/>, then grants if any is Global-scoped or if
    /// <paramref name="scopeGrants"/> accepts its (non-global) scope. Centralises the
    /// permission filter and the Global short-circuit so each caller only supplies the
    /// resource-specific scope rule.
    /// </summary>
    private async Task<bool> AnyGrantAsync(
        UserContext actor, ManagementPermission required,
        Func<ScopedRoleAssignment, Task<bool>> scopeGrants)
    {
        var assignments = await GetEffectiveAssignmentsAsync(actor);

        foreach (var (assignment, role) in assignments)
        {
            if (!HasPermission(role, required))
                continue;

            if (assignment.ScopeType == ScopeType.Global)
                return true;

            if (await scopeGrants(assignment))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if <paramref name="entityDepartmentId"/> is the same as
    /// <paramref name="scopeDepartmentId"/> or one of its descendants.
    ///
    /// Used for Group and Share scope checks: both now carry a direct
    /// DepartmentId FK, so instead of the old M:N lookup we just compare
    /// the entity's department against the scope hierarchy.
    /// </summary>
    private async Task<bool> IsDepartmentInScopeAsync(
        Guid entityDepartmentId, Guid scopeDepartmentId)
    {
        if (entityDepartmentId == scopeDepartmentId)
            return true;

        var descendants = await _departmentRepo.GetDescendantIdsAsync(scopeDepartmentId);
        return descendants.Contains(entityDepartmentId);
    }

    /// <summary>
    /// Collects ALL effective assignments from two sources:
    ///
    /// 1. DIRECT role assignments (actor.Roles) → synthetic Global-scoped entries.
    /// 2. SCOPED role assignments (ScopedRoleAssignment table) → from DB.
    ///
    /// Both are returned in the same format so all permission checks
    /// evaluate them identically.
    /// </summary>
    private async Task<List<(ScopedRoleAssignment Assignment, Role Role)>>
        GetEffectiveAssignmentsAsync(UserContext actor)
    {
        var result = new List<(ScopedRoleAssignment, Role)>();

        // -- 1. Direct role assignments → Global scope --
        foreach (var role in actor.Roles)
        {
            if (role.ManagementPermissions == ManagementPermission.None)
                continue;

            var synthetic = new ScopedRoleAssignment(
                actor.User.Id, role.Id, ScopeType.Global, Guid.Empty);

            result.Add((synthetic, role));
        }

        // -- 2. Scoped assignments from DB --
        var groupIds = actor.Groups?.Select(g => g.Id) ?? Enumerable.Empty<Guid>();
        var assignments = await _assignmentRepo.GetEffectiveAssignmentsAsync(
            actor.User.Id, groupIds);

        foreach (var assignment in assignments)
        {
            if (result.Any(r =>
                r.Item1.RoleId == assignment.RoleId &&
                r.Item1.ScopeType == ScopeType.Global))
                continue;

            var role = await _roleRepo.GetByIdAsync(assignment.RoleId);
            if (role != null)
                result.Add((assignment, role));
        }

        return result;
    }
}