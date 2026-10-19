using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Host, Web and Bridge talk only through this database, so a DB restart or a
    /// dropped pooled connection must not surface as a user-facing error. Transient
    /// failures are retried; explicit transactions therefore have to run through
    /// DbContextFactoryExtensions.ExecuteResilientAsync. Shared with the PostgreSQL
    /// tests so they run under exactly the production retry behavior.
    /// Every retry is logged as a warning with its cause: EF reports it only at Information,
    /// which the default "Warning" level hides, and a retried statement may already have been
    /// committed once (lost acknowledgement), so each one must be traceable.
    /// </summary>
    internal static void UseKaimoNpgsql(DbContextOptionsBuilder options, string connectionString)
        => options
            .UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(
                maxRetryCount: 3,
                maxRetryDelay: TimeSpan.FromSeconds(5),
                errorCodesToAdd: null))
            .ConfigureWarnings(warnings => warnings.Log(
                (Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ExecutionStrategyRetrying, LogLevel.Warning)));

    /// <summary>
    /// Adds connection defaults that every process needs but an operator rarely sets:
    /// kernel TCP keepalives so half-open connections (DB restart, NAT/bridge timeouts) are
    /// detected instead of hanging, also while a command is waiting for its result (Npgsql's
    /// own "Keepalive" only sends queries over idle connections), and an application name
    /// per container so <c>pg_stat_activity</c> shows which process holds a connection.
    /// Values that are already present in the configured connection string always win.
    /// </summary>
    internal static string BuildConnectionString(string connectionString, string? processName)
    {
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
        // Check the raw string, not the builder's properties: their defaults are also valid
        // explicit operator choices. Npgsql's own builder answers ContainsKey for every valid
        // keyword, so a generic builder is used; keys are compared without spaces because
        // Npgsql accepts both "Tcp Keepalive" and "TcpKeepAlive".
        var configured = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = connectionString }
            .Keys.Cast<string>()
            .Select(key => key.Replace(" ", string.Empty))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!configured.Overlaps(["TcpKeepalive", "TcpKeepaliveTime", "TcpKeepaliveInterval"]))
        {
            builder.TcpKeepAlive = true;
            builder.TcpKeepAliveTime = 30;
            builder.TcpKeepAliveInterval = 10;
        }
        if (string.IsNullOrEmpty(builder.ApplicationName) && !string.IsNullOrWhiteSpace(processName))
            builder.ApplicationName = "kaimo-" + processName;
        return builder.ConnectionString;
    }
}
