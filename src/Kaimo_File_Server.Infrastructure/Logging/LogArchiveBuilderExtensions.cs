using Kaimo_File_Server.Core.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Logging;

public static class LogArchiveBuilderExtensions
{
    // EF Core logs one Information "Executed DbCommand" line per query under this
    // category. In normal operation that is the large majority of archived volume
    // and buries the events an operator looks for, so it is capped at Warning below.
    private const string DbCommandCategory = "Microsoft.EntityFrameworkCore.Database.Command";

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
        // Console keeps Settings/KAIMO_LOG_LEVEL; this archive receives Information+
        // for every category except the EF command firehose (see ShouldArchive).
        builder.Logging.AddFilter<LogArchiveLoggerProvider>(ShouldArchive);
    }

    // The archive keeps Information+ for every category except the EF "Executed
    // DbCommand" firehose, which is kept at Warning+ so command errors are still
    // archived without one Information line per query.
    internal static bool ShouldArchive(string? category, LogLevel level)
    {
        if (level == LogLevel.None)
            return false;
        var minimum = category is not null && category.StartsWith(DbCommandCategory, StringComparison.Ordinal)
            ? LogLevel.Warning
            : LogLevel.Information;
        return level >= minimum;
    }
}
