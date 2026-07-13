using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kaimo_File_Server.Infrastructure.Logging;

public static class LoggingLevelBuilderExtensions
{
    /// <summary>
    /// Enables the DB-backed, live-reloadable global log level for this process.
    /// Adds the override configuration source (so a stored level beats
    /// <c>appsettings.json</c>), registers it in DI, and starts the reloader that
    /// polls the config store and pushes changes in. Call from every process
    /// (Web + SMB host). Requires <c>AddInfrastructure</c> (for the config store).
    /// </summary>
    public static void AddDynamicLogLevel(this IHostApplicationBuilder builder)
    {
        var source = new LoggingLevelConfigurationSource();

        // Added last → highest priority, so it overrides the appsettings Default.
        builder.Configuration.Sources.Add(source);

        // Same instance in DI so the reloader can push new values into it.
        builder.Services.AddSingleton(source);
        builder.Services.AddHostedService<LoggingLevelReloader>();
    }
}
