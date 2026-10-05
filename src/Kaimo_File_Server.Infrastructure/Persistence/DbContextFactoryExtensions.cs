using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Persistence;

/// <summary>
/// Runs a unit of work under the provider's execution strategy. PostgreSQL is configured with
/// <c>EnableRetryOnFailure</c>, which rejects user-initiated transactions outside a strategy
/// call. Every attempt gets a fresh context, so a retried attempt never sees tracked state or
/// a broken transaction left over from the failed one.
/// </summary>
public static class DbContextFactoryExtensions
{
    /// <summary>Log category for retries, replays and replay detections of database work.</summary>
    public const string LogCategory = "Kaimo_File_Server.Infrastructure.Database";

    public static async Task<T> ExecuteResilientAsync<T>(
        this IDbContextFactory<ApplicationDbContext> factory,
        Func<ApplicationDbContext, Task<T>> operation,
        CancellationToken cancellationToken = default,
        [CallerMemberName] string operationName = "")
    {
        // Creating a context does not open a connection; it only resolves the strategy.
        await using var probe = await factory.CreateDbContextAsync(cancellationToken);
        int attempt = 0;
        // The token overload also cancels the backoff delay between attempts (up to the
        // configured maximum retry delay), so a shutdown does not wait it out.
        return await probe.Database.CreateExecutionStrategy().ExecuteAsync(async _ =>
        {
            // The cause of the retry is logged by EF (ExecutionStrategyRetrying, raised to
            // Warning). This entry names the unit of work: a replay may run against state the
            // previous attempt already committed if only its acknowledgement was lost.
            if (++attempt > 1)
                probe.GetDatabaseLogger().LogWarning(
                    "Replaying database unit of work {Operation} (attempt {Attempt}) after a transient failure.",
                    operationName, attempt);

            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            return await operation(db);
        }, cancellationToken);
    }

    public static Task ExecuteResilientAsync(
        this IDbContextFactory<ApplicationDbContext> factory,
        Func<ApplicationDbContext, Task> operation,
        CancellationToken cancellationToken = default,
        [CallerMemberName] string operationName = "")
        => factory.ExecuteResilientAsync<bool>(async db =>
        {
            await operation(db);
            return true;
        }, cancellationToken, operationName);

    /// <summary>
    /// Logger for database retry/replay diagnostics, resolved from the context so repositories
    /// need no extra constructor dependency.
    /// </summary>
    public static ILogger GetDatabaseLogger(this DbContext db)
        => db.GetService<ILoggerFactory>().CreateLogger(LogCategory);
}
