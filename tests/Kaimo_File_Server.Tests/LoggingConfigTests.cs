using Kaimo_File_Server.Core.Logging;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Infrastructure.Logging;
using Kaimo_File_Server.Infrastructure.Persistence;
using Kaimo_File_Server.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Tests for the dynamic, DB-backed global log level: the reloadable configuration
/// source that feeds <c>Logging:LogLevel:Default</c> and the cross-process store that
/// persists the level. Also verifies the English <see cref="LogMessages"/> resource
/// actually resolves at runtime (guards against a resx manifest-name mismatch).
/// </summary>
public class LoggingConfigTests : DatabaseTestBase
{
    // ───────────────────────── configuration source ─────────────────────────

    [Fact]
    public void Source_maps_allowed_level_to_default_key()
    {
        var source = new LoggingLevelConfigurationSource();
        IConfiguration config = new ConfigurationBuilder().Add(source).Build();

        // Nothing pushed yet → no override, appsettings default would win.
        Assert.Null(config["Logging:LogLevel:Default"]);

        source.SetLevel("Debug");
        Assert.Equal("Debug", config["Logging:LogLevel:Default"]);

        source.SetLevel("Error");
        Assert.Equal("Error", config["Logging:LogLevel:Default"]);
    }

    [Fact]
    public void Source_ignores_unknown_level_and_clears_override()
    {
        var source = new LoggingLevelConfigurationSource();
        IConfiguration config = new ConfigurationBuilder().Add(source).Build();

        source.SetLevel("Information");
        Assert.Equal("Information", config["Logging:LogLevel:Default"]);

        // A garbage value must not override anything — fall back to appsettings.
        source.SetLevel("LoudAndProud");
        Assert.Null(config["Logging:LogLevel:Default"]);
    }

    [Fact]
    public void Source_raises_reload_token_on_change()
    {
        var source = new LoggingLevelConfigurationSource();
        IConfigurationRoot config = new ConfigurationBuilder().Add(source).Build();

        var fired = false;
        ChangeToken.OnChange(config.GetReloadToken, () => fired = true);

        source.SetLevel("Debug");

        Assert.True(fired, "Setting the level should raise the configuration change token.");
    }

    // ───────────────────────── config store (real DB) ─────────────────────────

    [Fact]
    public async Task Store_defaults_to_warning_when_unset()
    {
        var store = BuildStore();
        Assert.Equal("Warning", await store.GetLevelAsync());
    }

    [Fact]
    public async Task Store_round_trips_a_valid_level()
    {
        var store = BuildStore();

        await store.SetLevelAsync("Debug");

        // Read back through a fresh store instance (fresh scope + fresh context) —
        // proves the value actually landed in the store, not just the cache.
        Assert.Equal("Debug", await BuildStore().GetLevelAsync());
    }

    [Fact]
    public async Task Store_rejects_an_invalid_level()
    {
        var store = BuildStore();
        await store.SetLevelAsync("Information");

        await store.SetLevelAsync("nonsense"); // ignored

        Assert.Equal("Information", await BuildStore().GetLevelAsync());
    }

    // ───────────────────────── resource sanity ─────────────────────────

    [Fact]
    public void LogMessages_resource_resolves_at_runtime()
    {
        // A wrong resx manifest name would make these silently return null.
        Assert.False(string.IsNullOrEmpty(LogMessages.SmbStarted));
        Assert.Contains("{ShareCount}", LogMessages.SmbStarted);
        Assert.False(string.IsNullOrEmpty(LogMessages.DatabaseReady));
    }

    // ───────────────────────── helpers ─────────────────────────

    /// <summary>
    /// A real <see cref="LoggingConfigStore"/> over this test's database: a scope
    /// factory that resolves the real <see cref="ConfigRepository"/> (scoped context +
    /// shared memory cache) — every call reads/writes the actual ConfigSettings table.
    /// </summary>
    private LoggingConfigStore BuildStore()
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddScoped<ApplicationDbContext>(_ => DbFactory.CreateDbContext());
        services.AddScoped<IConfigRepository, ConfigRepository>();
        var provider = services.BuildServiceProvider();

        return new LoggingConfigStore(provider.GetRequiredService<IServiceScopeFactory>());
    }
}
