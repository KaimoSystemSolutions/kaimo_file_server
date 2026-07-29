using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kaimo_File_Server.Infrastructure.Logging;

public static class LoggingLevelBuilderExtensions
{
    /// <summary>
    /// Enables the live-reloadable global log level for this process.
    /// Priority is KAIMO_LOG_LEVEL environment override, Settings/DB value, then
    /// appsettings fallback. Requires AddInfrastructure for the config store.
    /// </summary>
    public static void AddDynamicLogLevel(this IHostApplicationBuilder builder)
    {
        var source = new LoggingLevelConfigurationSource(
            builder.Configuration[Core.Logging.LoggingConfigKeys.EnvironmentVariable]);

        // Added last → highest priority, so it overrides the appsettings Default.
        builder.Configuration.Sources.Add(source);

        // Same instance in DI so the reloader can push new values into it.
        builder.Services.AddSingleton(source);
        builder.Services.AddHostedService<LoggingLevelReloader>();
    }
}
