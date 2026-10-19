using Kaimo_File_Server.Core.Logging;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Backup;
using Kaimo_File_Server.Infrastructure.Persistence;
using Kaimo_File_Server.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Host-only database ownership: optionally applies a one-shot startup
    /// restore, takes a safety backup before pending migrations, applies
    /// migrations, and runs the seeder. Retries up to 5 times with a 3-second
    /// delay for cold-start scenarios (e.g. Docker Compose where the DB
    /// container isn't ready yet).
    ///
    /// Only the Host calls this. Web and SmbBridge call
    /// <see cref="WaitForDatabaseReadyAsync"/> instead and never migrate, so
    /// there is a single, well-defined owner of the schema.
    /// </summary>
    public static async Task MigrateSeedAndBackupAsync(this IHost host)
    {
        const int maxRetries = 5;

        var logger = host.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Kaimo_File_Server.Infrastructure.Database");

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                using var scope = host.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                // The connection is opened explicitly so it stays the same
                // physical session for the whole critical section — EF does not
                // close a connection it did not open, so the advisory lock is
                // held across restore + MigrateAsync() + the seeder (which share
                // this scoped DbContext / connection). The lock also blocks any
                // second Host instance from migrating concurrently.
                await db.Database.OpenConnectionAsync();
                try
                {
                    await db.Database.ExecuteSqlRawAsync(
                        "SELECT pg_advisory_lock(hashtext('kaimo_file_server_migrations'))");
                    // The lock belongs to this session. If the connection breaks, the retrying
                    // strategy silently reopens a new one without the lock; checked below.
                    int lockSession = await GetBackendPidAsync(db);

                    // Withdraw the previous run's ready marker as early as possible, so a
                    // Web/Bridge starting alongside this Host waits for this run's seed
                    // instead of trusting the last one. Only possible once the schema
                    // exists; on a fresh database there is no marker to withdraw.
                    // ponytail: a non-owner that checks before this Host has connected
                    // still passes on the old marker; put the build version into the
                    // marker if releases start to change seeding without a migration.
                    if ((await db.Database.GetAppliedMigrationsAsync()).Any())
                        await WriteSchemaReadyMarkerAsync(db, string.Empty);

                    // 1) One-shot startup restore (if requested via config),
                    //    before any migration. A .done marker prevents a repeat.
                    bool justRestored = await TryRestoreOnStartupAsync(host, logger);

                    // The restored data carries the ready marker of the backup's run. Withdraw
                    // it again, or a Web/Bridge starting now would trust it before this run's
                    // seed. (While the restore itself runs, a non-owner may still briefly see it.)
                    if (justRestored && (await db.Database.GetAppliedMigrationsAsync()).Any())
                        await WriteSchemaReadyMarkerAsync(db, string.Empty);

                    // 2) Pre-migration safety backup: only when there is
                    //    existing data (applied migrations) AND pending changes.
                    //    Skipped right after a restore (that dump IS the backup)
                    //    and on a fresh database (nothing to protect yet).
                    if (!justRestored)
                    {
                        var applied = await db.Database.GetAppliedMigrationsAsync();
                        var pending = await db.Database.GetPendingMigrationsAsync();
                        if (applied.Any() && pending.Any())
                        {
                            var backup = host.Services.GetRequiredService<IDatabaseBackupService>();
                            await backup.CreateBackupAsync(BackupTrigger.PreMigration);
                        }
                    }

                    // 3) Migrate + seed.
                    await EnsureMigrationLockHeldAsync(db, lockSession);
                    await db.Database.MigrateAsync();
                    logger.LogInformation(LogEvents.DatabaseReady, LogMessages.DatabaseReady);

                    // Withdraw the ready marker for the duration of the seed. A release can
                    // change seeding without adding a migration; Web/Bridge (re)started now
                    // must not run against a half-seeded database. Not possible earlier:
                    // config_settings does not exist before the first migration.
                    await WriteSchemaReadyMarkerAsync(db, string.Empty);

                    // Before the seeder stores the first NT hashes with the configured key.
                    Security.AesGcmNtHashProtector.ValidateKeyStrength(
                        host.Services.GetRequiredService<IConfiguration>()["NtHash:EncryptionKey"],
                        allowDevelopmentKey: host.Services.GetRequiredService<IHostEnvironment>().IsDevelopment(),
                        isFreshInstall: !await db.Users.AnyAsync(u => u.NtHash != ""),
                        logger);

                    var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
                    await seeder.SeedAsync();

                    // After seeding, so users created on this start get their home too.
                    // Never fatal: a missing pool mount must not keep the app from starting.
                    try
                    {
                        await scope.ServiceProvider.GetRequiredService<HomeDirectoryService>().BackfillAsync();
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Home-folder backfill failed");
                    }

                    // 4) Ready marker, written last: Web and SmbBridge start only once it
                    //    names the schema version they were built for, i.e. after the
                    //    seed, not merely after the migrations.
                    await EnsureMigrationLockHeldAsync(db, lockSession);
                    await WriteSchemaReadyMarkerAsync(
                        db, db.Database.GetMigrations().LastOrDefault() ?? string.Empty);
                }
                finally
                {
                    // Best effort: when the database is gone the session (and with it the
                    // lock) is gone too. An unlock error must not replace the real failure.
                    try
                    {
                        await db.Database.ExecuteSqlRawAsync(
                            "SELECT pg_advisory_unlock(hashtext('kaimo_file_server_migrations'))");
                    }
                    catch (Exception unlockError)
                    {
                        logger.LogWarning(unlockError, "Releasing the migration lock failed.");
                    }
                    await db.Database.CloseConnectionAsync();
                }

                return;
            }
            // Only retry failures that Npgsql classifies as transient (for
            // example while PostgreSQL is still starting). Migration and
            // seeding errors must surface immediately with their true cause.
            catch (Exception ex) when (IsTransientDatabaseFailure(ex))
            {
                if (attempt == maxRetries)
                    throw new InvalidOperationException(
                        "Database could not be reached after multiple attempts.", ex);

                logger.LogWarning(LogEvents.DatabaseNotReadyRetry, ex,
                    LogMessages.DatabaseNotReadyRetry, maxRetries - attempt);
                await Task.Delay(3000);
            }
        }
    }

    /// <summary>
    /// A database failure that waiting can fix. Operations that run under the retrying
    /// execution strategy (LINQ queries, SaveChanges, MigrateAsync) report an exhausted
    /// transient failure as <see cref="Microsoft.EntityFrameworkCore.Storage.RetryLimitExceededException"/>,
    /// which derives from <see cref="Exception"/>, not from <see cref="Npgsql.NpgsqlException"/>.
    /// </summary>
    internal static bool IsTransientDatabaseFailure(Exception ex)
        => ex is Npgsql.NpgsqlException { IsTransient: true }
            or Microsoft.EntityFrameworkCore.Storage.RetryLimitExceededException
            or MigrationLockLostException;

    private static Task<int> GetBackendPidAsync(ApplicationDbContext db)
        => db.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync();

    /// <summary>
    /// Throws <see cref="MigrationLockLostException"/> when the connection was reopened since
    /// the migration lock was taken, i.e. the lock is gone. The startup loop then runs the
    /// whole (idempotent) sequence again under a freshly acquired lock.
    /// ponytail: compares backend PIDs, so a reconnect that happens to get the same PID
    /// goes unnoticed; query pg_locks for the held advisory lock if a second Host is ever run.
    /// </summary>
    private static async Task EnsureMigrationLockHeldAsync(ApplicationDbContext db, int lockSession)
    {
        int session = await GetBackendPidAsync(db);
        if (session != lockSession)
            throw new MigrationLockLostException(
                $"The database connection was re-established (session {lockSession} -> {session}); " +
                "the migration lock was lost and is acquired again.");
    }

    /// <summary>
    /// <c>config_settings</c> key the Host writes after migrating and seeding. Its value
    /// is the last migration id of the Host's build.
    /// </summary>
    internal const string SchemaReadyKey = "system.schema.ready";

    // Written without change tracking: the Host calls this several times on one context, and
    // a startup restore in between replaces the table, so a tracked row would be stale.
    private static async Task WriteSchemaReadyMarkerAsync(ApplicationDbContext db, string value)
    {
        var now = DateTime.UtcNow;
        var updated = await db.ConfigSettings
            .Where(s => s.Key == SchemaReadyKey)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Value, value)
                .SetProperty(x => x.UpdatedAt, now));
        if (updated > 0)
            return;

        var marker = new Configuration.ConfigSetting { Key = SchemaReadyKey, Value = value, UpdatedAt = now };
        db.ConfigSettings.Add(marker);
        await db.SaveChangesAsync();
        db.Entry(marker).State = EntityState.Detached;
    }

    /// <summary>
    /// Readiness of a non-owner process against the current database state.
    /// Returns <c>null</c> when ready, otherwise the reason to keep waiting. Throws
    /// <see cref="SchemaVersionMismatchException"/> when the database already carries
    /// migrations this build does not know: an older image must never write to a newer
    /// schema, and waiting cannot fix it.
    /// </summary>
    internal static async Task<string?> CheckSchemaReadyAsync(ApplicationDbContext db)
    {
        var known = db.Database.GetMigrations().ToList();
        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
        var unknown = applied.Except(known, StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
            throw new SchemaVersionMismatchException(
                "This container image is older than the database schema (unknown migrations: " +
                string.Join(", ", unknown) + "). Update it to the same version as the Host.");

        var pending = known.Except(applied, StringComparer.Ordinal).Count();
        if (pending > 0)
            return $"{pending} migration(s) pending";

        var marker = await db.ConfigSettings.AsNoTracking()
            .Where(s => s.Key == SchemaReadyKey)
            .Select(s => s.Value)
            .FirstOrDefaultAsync();
        var expected = known.LastOrDefault() ?? string.Empty;
        return string.Equals(marker, expected, StringComparison.Ordinal)
            ? null
            : "seeding not finished yet";
    }

    /// <summary>
    /// Non-owner processes (Web, SmbBridge): wait until the Host has applied
    /// all migrations and finished seeding (ready marker), then continue. Never
    /// migrates or seeds. This is the single, orchestration-independent readiness
    /// guarantee — it works regardless of Docker Compose ordering. Fails at once
    /// when this image is older than the schema; otherwise times out after
    /// 10 minutes. Then verifies this process holds the same NT-hash key as the
    /// stored data (<see cref="Security.NtHashKeyCanary"/>) and throws otherwise.
    /// </summary>
    public static async Task WaitForDatabaseReadyAsync(this IHost host)
    {
        var logger = host.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Kaimo_File_Server.Infrastructure.Database");

        var pollDelay = TimeSpan.FromSeconds(3);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(10);

        while (true)
        {
            try
            {
                using var scope = host.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var waitReason = await CheckSchemaReadyAsync(db);
                if (waitReason is null)
                {
                    logger.LogInformation(LogEvents.DatabaseReady, LogMessages.DatabaseReady);
                    break;
                }

                logger.LogInformation(
                    "Waiting for the database owner (Host): {Reason}.", waitReason);
            }
            catch (Exception ex) when (ex is Npgsql.NpgsqlException
                                       || IsTransientDatabaseFailure(ex)
                                       || ex is InvalidOperationException and not SchemaVersionMismatchException)
            {
                logger.LogInformation(
                    "Waiting for the database to become reachable: {Message}", ex.Message);
            }

            if (DateTimeOffset.UtcNow > deadline)
                throw new InvalidOperationException(
                    "Database was not ready (migrations pending or seeding not finished) after waiting. " +
                    "Ensure the Host process is running, able to migrate, and on the same version.");

            await Task.Delay(pollDelay);
        }

        // Outside the retry loop: a key mismatch must stop the process, not be retried.
        Security.AesGcmNtHashProtector.ValidateKeyStrength(
            host.Services.GetRequiredService<IConfiguration>()["NtHash:EncryptionKey"],
            allowDevelopmentKey: host.Services.GetRequiredService<IHostEnvironment>().IsDevelopment(),
            isFreshInstall: false, // the Host enforces the fresh-install length rule
            logger);
        using (var scope = host.Services.CreateScope())
        {
            await Security.NtHashKeyCanary.VerifyAsync(
                scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
                scope.ServiceProvider.GetRequiredService<INtHashProtector>(),
                logger);
        }
    }
}
