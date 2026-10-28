using System.Collections.Concurrent;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;

namespace Kaimo_File_Server.Core.Services;

/// <summary>
/// Lookups of <see cref="ManagementAuthService"/> and the evaluation scope that caches them.
///
/// The service is scoped, which in Blazor Server means "per circuit", so nothing is cached on the
/// instance: without an open scope every check reads the store again and a revoked assignment or
/// group membership takes effect immediately. An evaluation scope (opened by a composite check
/// itself, or by a caller around a read-only batch of checks) caches the actor's assignments, roles,
/// the department tree, shares and the assignments of checked target users until it is disposed. It is tracked per async flow, so parallel
/// flows never share a cache.
/// </summary>
public partial class ManagementAuthService
{
    private readonly AsyncLocal<EvaluationCache?> _evaluation = new();

    public IDisposable BeginEvaluationScope()
    {
        // A nested scope reuses the outer cache; only the outermost one clears it.
        if (_evaluation.Value is not null)
            return NestedScope.Instance;
        _evaluation.Value = new EvaluationCache();
        return new OwningScope(this);
    }

    private sealed class EvaluationCache
    {
        public ConcurrentDictionary<Guid, List<(ScopedRoleAssignment Assignment, Role Role)>> Assignments { get; } = new();
        public ConcurrentDictionary<Guid, Role?> Roles { get; } = new();
        public ConcurrentDictionary<Guid, HashSet<Guid>> Descendants { get; } = new();
        public ConcurrentDictionary<Guid, ShareDefinition?> Shares { get; } = new();
        public ConcurrentDictionary<Guid, List<ShareDefinition>> DepartmentShares { get; } = new();
        public ConcurrentDictionary<Guid, List<ScopedRoleAssignment>> TargetAssignments { get; } = new();
    }

    private sealed class OwningScope(ManagementAuthService owner) : IDisposable
    {
        public void Dispose() => owner._evaluation.Value = null;
    }

    private sealed class NestedScope : IDisposable
    {
        public static readonly NestedScope Instance = new();
        public void Dispose() { }
    }

    /// <summary>Returns the cached value inside an evaluation scope, otherwise always loads.</summary>
    private async Task<T> CachedAsync<T>(
        Func<EvaluationCache, ConcurrentDictionary<Guid, T>> select, Guid key, Func<Task<T>> load)
    {
        if (_evaluation.Value is not { } cache)
            return await load();
        var map = select(cache);
        if (map.TryGetValue(key, out var cached))
            return cached;
        var value = await load();
        map[key] = value;
        return value;
    }

    private Task<Role?> GetRoleAsync(Guid roleId)
        => CachedAsync(c => c.Roles, roleId, () => _roleRepo.GetByIdAsync(roleId));

    // Callers must not modify the returned set: inside a scope it is shared.
    private Task<HashSet<Guid>> GetDescendantIdsAsync(Guid departmentId)
        => CachedAsync(c => c.Descendants, departmentId, () => _departmentRepo.GetDescendantIdsAsync(departmentId));

    private Task<ShareDefinition?> GetShareAsync(Guid shareId)
        => CachedAsync(c => c.Shares, shareId, () => _shareRepo.GetByIdAsync(shareId));

    private Task<List<ShareDefinition>> GetDepartmentSharesAsync(Guid departmentId)
        => CachedAsync(c => c.DepartmentShares, departmentId, () => _departmentRepo.GetSharesAsync(departmentId));

    /// <summary>The effective assignments of a target user (own and via groups), for the rank check.</summary>
    private Task<List<ScopedRoleAssignment>> GetTargetAssignmentsAsync(Guid targetUserId)
        => CachedAsync(c => c.TargetAssignments, targetUserId, async () =>
        {
            var groups = await _userRepo.GetGroupsForUserAsync(targetUserId) ?? [];
            return await _assignmentRepo.GetEffectiveAssignmentsAsync(
                targetUserId, groups.Select(g => g.Id).ToList()) ?? [];
        });

    /// <summary>
    /// Collects the actor's effective role assignments — the user's own and those inherited via
    /// group membership — each paired with its resolved <see cref="Role"/>. Group memberships are
    /// read from the store, not from the <see cref="UserContext"/> snapshot, so a view model that
    /// keeps the context of its page load cannot use a group it has been removed from since.
    /// </summary>
    private Task<List<(ScopedRoleAssignment Assignment, Role Role)>> GetEffectiveAssignmentsAsync(UserContext actor)
        => CachedAsync(c => c.Assignments, actor.User.Id, async () =>
        {
            var groups = await _userRepo.GetGroupsForUserAsync(actor.User.Id);
            var groupIds = (groups ?? actor.Groups?.ToList() ?? []).Select(g => g.Id).ToList();
            var assignments = await _assignmentRepo.GetEffectiveAssignmentsAsync(actor.User.Id, groupIds);

            var result = new List<(ScopedRoleAssignment, Role)>();
            foreach (var assignment in assignments)
            {
                if (await GetRoleAsync(assignment.RoleId) is { } role)
                    result.Add((assignment, role));
            }
            return result;
        });
}
