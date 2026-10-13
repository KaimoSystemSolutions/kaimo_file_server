using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Configuration;
using Microsoft.Extensions.Caching.Memory;

namespace Kaimo_File_Server.Infrastructure.Services;

/// <summary>
/// Evaluates the <see cref="AccessRestrictionSettings"/> for a user and a request (client IP
/// and host). Without an enabled rule it returns immediately, so installations that do not use
/// the feature pay no database cost. The rules and a user's groups and departments (including
/// department ancestors) are cached for <see cref="PrincipalCacheTtl"/>.
/// </summary>
public sealed class AccessRestrictionService(
    IConfigRepository config,
    IUserContextFactory contexts,
    IDepartmentRepository departments,
    IMemoryCache cache)
{
    private static readonly TimeSpan PrincipalCacheTtl = TimeSpan.FromSeconds(30);
    private const string SettingsCacheKey = "access-restrictions:settings";

    private sealed record Principals(IReadOnlyCollection<Guid> GroupIds, IReadOnlyCollection<Guid> DepartmentIds);

    /// <summary>
    /// The current rules. Cached here as well, because the config store does not cache a
    /// missing key and this runs on every authenticated request.
    /// </summary>
    public async Task<AccessRestrictionSettings> GetSettingsAsync()
        => (await cache.GetOrCreateAsync(SettingsCacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = PrincipalCacheTtl;
            return await config.GetAsync(AccessRestrictionSettings.ConfigKey, AccessRestrictionSettings.Default())
                   ?? AccessRestrictionSettings.Default();
        }))!;

    public async Task SaveSettingsAsync(AccessRestrictionSettings settings)
    {
        await config.SetAsync(AccessRestrictionSettings.ConfigKey, settings);
        cache.Remove(SettingsCacheKey);
    }

    /// <summary>Whether the user may access the server from this address and host.</summary>
    public async Task<bool> IsAllowedAsync(Guid userId, string? remoteAddress, string? host)
    {
        if (AccessRestrictionSettings.DisabledByEnvironment) return true;
        var settings = await GetSettingsAsync();
        if (!settings.HasEnabledRules) return true;

        var principals = await cache.GetOrCreateAsync(("access-principals", userId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = PrincipalCacheTtl;
            var context = await contexts.CreateByUserIdAsync(userId);
            return context is null ? new Principals([], []) : await ResolveAsync(context);
        });
        return settings.IsAllowed(userId, principals!.GroupIds, principals.DepartmentIds, remoteAddress, host);
    }

    /// <summary>
    /// Like <see cref="IsAllowedAsync(Guid, string?, string?)"/> for an already resolved user,
    /// optionally against <paramref name="candidate"/> settings that are not saved yet.
    /// </summary>
    public async Task<bool> IsAllowedAsync(
        UserContext user, string? remoteAddress, string? host, AccessRestrictionSettings? candidate = null)
    {
        if (AccessRestrictionSettings.DisabledByEnvironment) return true;
        var settings = candidate ?? await GetSettingsAsync();
        if (!settings.HasEnabledRules) return true;

        var principals = await ResolveAsync(user);
        return settings.IsAllowed(user.User.Id, principals.GroupIds, principals.DepartmentIds, remoteAddress, host);
    }

    private async Task<Principals> ResolveAsync(UserContext user)
    {
        var departmentIds = new HashSet<Guid>();
        foreach (var department in user.Departments)
        {
            departmentIds.Add(department.Id);
            foreach (var ancestor in await departments.GetAncestorChainAsync(department.Id))
                departmentIds.Add(ancestor.Id);
        }
        return new Principals(user.Groups.Select(g => g.Id).ToHashSet(), departmentIds);
    }
}
