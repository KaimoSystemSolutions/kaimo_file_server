using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Search;
using Kaimo_File_Server.Tests.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// <see cref="SearchConfigStore.HasSearchStateAsync"/> over the real schema: only an install that
/// never stored any search key (engine, legacy Elasticsearch flag, an index cursor, a build marker)
/// counts as fresh.
/// </summary>
public class SearchConfigStoreTests : DatabaseTestBase
{
    private readonly ConfigRepository _config;
    private readonly SearchConfigStore _store;

    public SearchConfigStoreTests()
    {
        _config = new ConfigRepository(DbFactory, new MemoryCache(new MemoryCacheOptions()));
        var services = new ServiceCollection().AddSingleton<IConfigRepository>(_config).BuildServiceProvider();
        _store = new SearchConfigStore(services.GetRequiredService<IServiceScopeFactory>());
    }

    [Fact]
    public async Task FreshInstall_HasNoState()
        => Assert.False(await _store.HasSearchStateAsync());

    [Fact]
    public async Task EngineStored_HasState()
    {
        await _store.SetEngineAsync(SearchEngine.Filename);
        Assert.True(await _store.HasSearchStateAsync());
    }

    [Fact]
    public async Task OnlyLegacyFlagStored_HasState()
    {
        // An install from before engine selection that turned Elasticsearch off keeps Filename.
        await _config.SetAsync(SearchConfigKeys.ElasticEnabledKey, false);
        Assert.True(await _store.HasSearchStateAsync());
    }

    [Fact]
    public async Task OnlyElasticCursorStored_HasState()
    {
        // An install that always ran on the default (Elasticsearch) never stored an engine, but its
        // indexer persisted a cursor: it is not a fresh install and must keep Elasticsearch.
        await _store.SetIndexCursorAsync(SearchEngine.Elasticsearch, 17);
        Assert.True(await _store.HasSearchStateAsync());
    }

    [Fact]
    public async Task UnrelatedKeysOnly_HasNoState()
    {
        await _config.SetAsync("smb.enabled", true);
        Assert.False(await _store.HasSearchStateAsync());
    }
}
