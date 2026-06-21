using Kaimo_File_Server.Search;
using Microsoft.Extensions.DependencyInjection;

namespace Kaimo_File_Server.Infrastructure.Configuration;

/// <summary>
/// Implements <see cref="ISearchConfigStore"/> over <see cref="IConfigRepository"/>.
/// Reads use <see cref="IConfigRepository.GetFreshAsync"/> because the flag is set
/// in the Web process but read in the SMB host process — a cached value would hide
/// the change for up to the cache TTL. The search router additionally caches the
/// result for a few seconds, so this does not turn into a per-write DB hit.
/// </summary>
public sealed class SearchConfigStore : ISearchConfigStore
{
    private readonly IServiceScopeFactory _scopeFactory;

    public SearchConfigStore(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<bool> GetElasticEnabledAsync(bool fallback = true)
    {
        using var scope = _scopeFactory.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        return await config.GetFreshAsync(SearchConfigKeys.ElasticEnabledKey, fallback);
    }

    public async Task SetElasticEnabledAsync(bool enabled)
    {
        using var scope = _scopeFactory.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        await config.SetAsync(SearchConfigKeys.ElasticEnabledKey, enabled);
    }
}
