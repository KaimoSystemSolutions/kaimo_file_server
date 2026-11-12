using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories.Identity;
using Kaimo_File_Server.Core.Security.Authentication;
using Kaimo_File_Server.Core.Security.Management;
using Kaimo_File_Server.Infrastructure.Configuration;
using Microsoft.Extensions.Caching.Memory;

namespace Kaimo_File_Server.Infrastructure.Services;

/// <summary>
/// Evaluates the <see cref="SecondFactorPolicy"/> of the web login for a user: which login step
/// follows a correct password, and whether a web session needs a token that was confirmed with
/// a second factor. The policy and each user's result are cached for <see cref="CacheTtl"/>, so
/// the periodic session revalidation costs no database round-trip per tick.
/// </summary>
public sealed class SecondFactorPolicyService(
    IConfigRepository config,
    IUserContextFactory contexts,
    IDepartmentRepository departments,
    IScopedRoleAssignmentRepository assignments,
    IRoleRepository roles,
    ISecondFactorRepository factors,
    IMemoryCache cache)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    private const string PolicyCacheKey = "second-factor:policy";

    // Part of every session cache key: a policy save starts a new generation, so no user's
    // result from the previous policy is used again (IMemoryCache cannot drop keys by prefix).
    // ponytail: per process, which is enough since only the web host evaluates the policy.
    private static long _generation;

    private sealed record Principals(
        IReadOnlyCollection<Guid> GroupIds, IReadOnlyCollection<Guid> RoleIds,
        IReadOnlyCollection<Guid> DepartmentIds, bool HasManagementPermission);

    public async Task<SecondFactorPolicy> GetPolicyAsync()
        => (await cache.GetOrCreateAsync(PolicyCacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            return await config.GetAsync(SecondFactorPolicy.ConfigKey, SecondFactorPolicy.Default())
                   ?? SecondFactorPolicy.Default();
        }))!;

    public async Task SavePolicyAsync(SecondFactorPolicy policy)
    {
        await config.SetAsync(SecondFactorPolicy.ConfigKey, policy);
        cache.Remove(PolicyCacheKey);
        Interlocked.Increment(ref _generation);
    }

    /// <summary>Drops the cached session result of a user (after set-up, removal or reset).</summary>
    public void Invalidate(Guid userId) => cache.Remove(SessionCacheKey(userId));

    /// <summary>Whether users may set up a second factor at all (the policy is not off).</summary>
    public async Task<bool> IsAvailableAsync()
        => !SecondFactorPolicy.DisabledByEnvironment && (await GetPolicyAsync()).Mode != SecondFactorMode.Off;

    /// <summary>
    /// Whether the policy (or <paramref name="candidate"/>, a policy not saved yet) requires a
    /// second factor for this user. Ignores the break-glass switch, so the admin UI shows the
    /// configured state.
    /// </summary>
    public async Task<bool> IsRequiredAsync(UserContext user, SecondFactorPolicy? candidate = null)
    {
        var policy = candidate ?? await GetPolicyAsync();
        if (policy.Mode != SecondFactorMode.Required)
            return false;
        var p = await ResolveAsync(user);
        return policy.Requires(user.User.Id, p.GroupIds, p.RoleIds, p.DepartmentIds, p.HasManagementPermission);
    }

    /// <summary>The step that follows a correct password for this user.</summary>
    public async Task<SecondFactorLoginStep> GetLoginStepAsync(UserContext user)
    {
        if (!await IsAvailableAsync())
            return SecondFactorLoginStep.None;
        var factor = await factors.GetAsync(user.User.Id);
        if (factor?.ConfirmedAtUtc is not null)
            return SecondFactorLoginStep.Verify;
        return await IsRequiredAsync(user) ? SecondFactorLoginStep.Enroll : SecondFactorLoginStep.None;
    }

    /// <summary>
    /// Whether a web session of this user is only valid with a token confirmed by a second
    /// factor. Cached per user; a token from before a policy change thus ends within the TTL.
    /// </summary>
    public async Task<bool> SessionRequiresSecondFactorAsync(Guid userId)
        => await cache.GetOrCreateAsync(SessionCacheKey(userId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            var context = await contexts.CreateByUserIdAsync(userId);
            return context is not null && await GetLoginStepAsync(context) != SecondFactorLoginStep.None;
        });

    private static string SessionCacheKey(Guid userId)
        => $"second-factor:session:{Interlocked.Read(ref _generation)}:{userId:N}";

    private async Task<Principals> ResolveAsync(UserContext user)
    {
        var departmentIds = new HashSet<Guid>();
        foreach (var department in user.Departments)
        {
            departmentIds.Add(department.Id);
            foreach (var ancestor in await departments.GetAncestorChainAsync(department.Id))
                departmentIds.Add(ancestor.Id);
        }

        var groupIds = user.Groups.Select(g => g.Id).ToHashSet();
        var roleIds = (await assignments.GetEffectiveAssignmentsAsync(user.User.Id, groupIds))
            .Select(a => a.RoleId).ToHashSet();
        bool hasManagementPermission = false;
        foreach (var roleId in roleIds)
        {
            if (await roles.GetByIdAsync(roleId) is { ManagementPermissions: not ManagementPermission.None })
            {
                hasManagementPermission = true;
                break;
            }
        }
        return new Principals(groupIds, roleIds, departmentIds, hasManagementPermission);
    }
}
