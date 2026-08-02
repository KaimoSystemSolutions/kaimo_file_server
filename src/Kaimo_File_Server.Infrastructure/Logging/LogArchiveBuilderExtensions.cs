using Kaimo_File_Server.Core.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Logging;

public static class LogArchiveBuilderExtensions
{
    /// <summary>Adds the fixed Information+ archive without changing console filter semantics.</summary>
    public static void AddLogArchive(this IHostApplicationBuilder builder, string source)
    {
        builder.Services.AddOptions<LogArchiveOptions>()
            .Bind(builder.Configuration.GetSection(LogArchiveOptions.SectionName))
            .PostConfigure(options =>
            {
                options.Source = Environment.GetEnvironmentVariable("KAIMO_LOG_SOURCE") ?? source;
                options.Normalize();
            });

        builder.Services.AddSingleton<LogArchiveLoggerProvider>();
        builder.Services.AddSingleton<ILoggerProvider>(provider =>
            provider.GetRequiredService<LogArchiveLoggerProvider>());
        builder.Services.AddHostedService(provider =>
            provider.GetRequiredService<LogArchiveLoggerProvider>());
        builder.Services.AddSingleton<ILogArchiveReader, FileLogArchiveReader>();

        // A provider-specific rule wins over the existing dynamic global default.
        // Console keeps Settings/KAIMO_LOG_LEVEL; this archive always receives Information+.
        builder.Logging.AddFilter<LogArchiveLoggerProvider>(
            (_, level) => level >= LogLevel.Information && level != LogLevel.None);
    }
}
