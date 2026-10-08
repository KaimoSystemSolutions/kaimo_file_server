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

    public Task<long> GetIndexCursorAsync() => GetIndexCursorAsync(SearchEngine.Elasticsearch);

    public Task SetIndexCursorAsync(long seq) => SetIndexCursorAsync(SearchEngine.Elasticsearch, seq);

    public async Task<SearchEngine> GetEngineAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        return SearchConfigKeys.ParseEngine(
            await config.GetFreshAsync(SearchConfigKeys.EngineKey, string.Empty),
            await config.GetFreshAsync(SearchConfigKeys.ElasticEnabledKey, true));
    }

    public async Task SetEngineAsync(SearchEngine engine)
    {
        using var scope = _scopeFactory.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        await config.SetAsync(SearchConfigKeys.EngineKey, engine.ToString());
        // Keep the legacy flag in step so a downgrade to a version without engine selection
        // still uses Elasticsearch exactly when it was selected.
        await config.SetAsync(SearchConfigKeys.ElasticEnabledKey, engine == SearchEngine.Elasticsearch);
    }

    public async Task<long> GetIndexCursorAsync(SearchEngine engine)
    {
        using var scope = _scopeFactory.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        return await config.GetFreshAsync(SearchConfigKeys.CursorKeyFor(engine), 0L);
    }

    public async Task SetIndexCursorAsync(SearchEngine engine, long seq)
    {
        using var scope = _scopeFactory.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        await config.SetAsync(SearchConfigKeys.CursorKeyFor(engine), seq);
    }

    public async Task<bool> GetLocalIndexBuiltAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        return await config.GetFreshAsync(SearchConfigKeys.LocalIndexBuiltKey, 0)
               == SearchConfigKeys.LocalIndexVersion;
    }

    public async Task SetLocalIndexBuiltAsync(bool built)
    {
        using var scope = _scopeFactory.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        await config.SetAsync(SearchConfigKeys.LocalIndexBuiltKey, built ? SearchConfigKeys.LocalIndexVersion : 0);
    }
}
