using Kaimo_File_Server.Core.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Kaimo_File_Server.Infrastructure.Configuration;

/// <summary>
/// Implements <see cref="ILoggingConfigStore"/> over <see cref="IConfigRepository"/>.
/// Reads use <see cref="IConfigRepository.GetFreshAsync"/> because the level is written
/// in the Web process but read (via the reloader) in the SMB host process — a cached
/// value would hide the change for up to the cache TTL. Mirrors <see cref="SmbConfigStore"/>.
/// </summary>
public sealed class LoggingConfigStore : ILoggingConfigStore
{
    private readonly IServiceScopeFactory _scopeFactory;

    public LoggingConfigStore(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<string> GetLevelAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<IConfigRepository>();

        var level = await config.GetFreshAsync(LoggingConfigKeys.LevelKey, LoggingConfigKeys.DefaultLevel);

        // A hand-edited store value could be garbage; never hand an unknown level
        // to the reloader (it would clear the override and silently drop to appsettings).
        return LoggingConfigKeys.IsAllowed(level) ? level : LoggingConfigKeys.DefaultLevel;
    }

    public async Task SetLevelAsync(string level)
    {
        if (!LoggingConfigKeys.IsAllowed(level))
            return;

        using var scope = _scopeFactory.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        await config.SetAsync(LoggingConfigKeys.LevelKey, level);
    }
}
