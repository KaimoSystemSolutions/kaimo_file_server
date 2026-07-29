using Kaimo_File_Server.Core.Logging;
using Microsoft.Extensions.Configuration;

namespace Kaimo_File_Server.Infrastructure.Logging;

/// <summary>
/// Reloadable source for <c>Logging:LogLevel:Default</c>.
/// Priority is the process-level <c>KAIMO_LOG_LEVEL</c> override, then the
/// Settings/DB value, then the normal appsettings fallback.
/// </summary>
public sealed class LoggingLevelConfigurationSource : IConfigurationSource
{
    private readonly string? _manualOverrideLevel;
    private LoggingLevelConfigurationProvider? _provider;

    public LoggingLevelConfigurationSource(string? manualOverrideLevel = null)
    {
        var normalizedOverride = LoggingConfigKeys.Normalize(manualOverrideLevel);
        if (!string.IsNullOrWhiteSpace(manualOverrideLevel) &&
            normalizedOverride is null)
        {
            throw new InvalidOperationException(
                $"{LoggingConfigKeys.EnvironmentVariable} must be one of: " +
                string.Join(", ", LoggingConfigKeys.AllowedLevels));
        }

        _manualOverrideLevel = normalizedOverride;
    }

    /// <summary>The active environment override, if configured.</summary>
    public string? ManualOverrideLevel => _manualOverrideLevel;

    /// <summary>Resolves environment override before the supplied DB value.</summary>
    public string? ResolveLevel(string? databaseLevel)
        => _manualOverrideLevel ?? LoggingConfigKeys.Normalize(databaseLevel);

    public IConfigurationProvider Build(IConfigurationBuilder builder)
        => _provider ??= new LoggingLevelConfigurationProvider(_manualOverrideLevel);

    /// <summary>
    /// Pushes a new Settings/DB value. An active environment override remains
    /// authoritative.
    /// </summary>
    public void SetLevel(string? level) => _provider?.SetDatabaseLevel(level);

    private sealed class LoggingLevelConfigurationProvider : ConfigurationProvider
    {
        private readonly string? _manualOverrideLevel;

        public LoggingLevelConfigurationProvider(string? manualOverrideLevel)
        {
            _manualOverrideLevel = manualOverrideLevel;
        }

        public override void Load() => Apply(databaseLevel: null, reload: false);

        public void SetDatabaseLevel(string? level)
            => Apply(level, reload: true);

        private void Apply(string? databaseLevel, bool reload)
        {
            var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var effectiveLevel =
                _manualOverrideLevel ?? LoggingConfigKeys.Normalize(databaseLevel);

            if (effectiveLevel is not null)
                data["Logging:LogLevel:Default"] = effectiveLevel;

            Data = data;
            if (reload)
                OnReload();
        }
    }
}
