using Kaimo_File_Server.Core.Logging;
using Microsoft.Extensions.Configuration;

namespace Kaimo_File_Server.Infrastructure.Logging;

/// <summary>
/// An in-memory, reloadable configuration source that contributes a single
/// <c>Logging:LogLevel:Default</c> entry — the global application log level stored
/// in the database. It is added to the host's configuration AFTER the JSON/env
/// sources so it overrides the <c>appsettings.json</c> default, and is also
/// registered in DI so <see cref="LoggingLevelReloader"/> can push new values into
/// it. Pushing a value raises the configuration change token, which the logging
/// framework watches — so the effective filter changes live, no restart.
///
/// When no level has been pushed yet (startup, or the DB is unreachable) it
/// contributes nothing and the <c>appsettings.json</c> default applies. More
/// specific category rules from <c>appsettings.json</c> (e.g. the EF Core
/// suppressions) are never touched, so they keep damping ORM noise even at Debug.
/// </summary>
public sealed class LoggingLevelConfigurationSource : IConfigurationSource
{
    private LoggingLevelConfigurationProvider? _provider;

    public IConfigurationProvider Build(IConfigurationBuilder builder)
        => _provider ??= new LoggingLevelConfigurationProvider();

    /// <summary>
    /// Sets the global level and triggers a live reload. Ignored (and the current
    /// override cleared) when <paramref name="level"/> is not an allowed level name.
    /// </summary>
    public void SetLevel(string? level) => _provider?.SetLevel(level);

    private sealed class LoggingLevelConfigurationProvider : ConfigurationProvider
    {
        // Load() is called once when the source is built; keep whatever has been set.
        public override void Load() { }

        public void SetLevel(string? level)
        {
            var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (LoggingConfigKeys.IsAllowed(level))
                data["Logging:LogLevel:Default"] = level;

            Data = data;
            OnReload(); // fires the change token → LoggerFilterOptions rebind
        }
    }
}
