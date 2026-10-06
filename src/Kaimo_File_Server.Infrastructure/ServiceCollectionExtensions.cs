using Kaimo_File_Server.Core.Logging;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Storage;
using Kaimo_File_Server.Infrastructure.Backup;
using Kaimo_File_Server.Infrastructure.Clouds;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Infrastructure.ExternalStorage;
using Kaimo_File_Server.Infrastructure.Persistence;
using Kaimo_File_Server.Infrastructure.Repositories;
using Kaimo_File_Server.Infrastructure.Services;
using Kaimo_File_Server.Infrastructure.Storage;
using Kaimo_File_Server.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure
{
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Registers all Infrastructure services: DB context, repositories,
        /// password hashing, user context factory, management auth, and
        /// authentication lookup.
        ///
        /// Call this once from every host (Host, Web). Do NOT re-register
        /// these services in individual Program.cs files.
        /// </summary>
        public static IServiceCollection AddInfrastructure(
            this IServiceCollection services, IConfiguration configuration)
        {
            var connectionString = BuildConnectionString(
                configuration.GetConnectionString("Default")
                    ?? "Host=kaimo_file_server_db;Database=kaimo_file_server;Username=kaimo_test_user;Password=change_me",
                configuration["KAIMO_LOG_SOURCE"]);

            // Read-only demo mode: set KAIMO_DEMO_READONLY=true on a PUBLIC-facing
            // process (Web/SmbBridge) to turn it into a look-but-don't-touch demo.
            // Leave it UNSET on the Host so seeding, migrations and background jobs
            // (backups, data-service reconcile) keep writing normally.
            var readOnlyDemo = configuration.GetValue<bool>("KAIMO_DEMO_READONLY");
            // Presentation flag (hide write actions, show feedback banner). The hard
            // write blocking is done by the interceptor + ACL guard below.
            services.AddSingleton(new DemoModeOptions { ReadOnly = readOnlyDemo });

            // -- EF Core --
            services.AddDbContextFactory<ApplicationDbContext>(options =>
            {
                UseKaimoNpgsql(options, connectionString);
                if (readOnlyDemo)
                    options.AddInterceptors(new Persistence.ReadOnlyDemoSaveInterceptor());
            });

            // -- Repositories --
            services.AddScoped<IUserRepository, UserRepository>();
            services.AddScoped<IShareRepository, ShareRepository>();
            services.AddScoped<IShareLinkRepository, ShareLinkRepository>();
            services.AddScoped<ICloudAccessRepository, CloudAccessRepository>();
            services.AddScoped<IStorageConnectionRepository, StorageConnectionRepository>();
            services.AddScoped<ISyncDefinitionRepository, SyncDefinitionRepository>();
            services.AddScoped<IGroupRepository, GroupRepository>();
            services.AddScoped<IRoleRepository, RoleRepository>();
            services.AddScoped<IDepartmentRepository, DepartmentRepository>();
            services.AddScoped<IScopedRoleAssignmentRepository, ScopedRoleAssignmentRepository>();
            services.AddScoped<IFileVersionRepository, FileVersionRepository>();
            services.AddScoped<ISambaLifecycleEventRepository, SambaLifecycleEventRepository>();

            // -- Client API (mobile/desktop apps): device registrations, refresh
            //    tokens, per-device sync selections, and the change cursor --
            services.AddScoped<ISyncDeviceRepository, SyncDeviceRepository>();
            services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
            services.AddScoped<IRevokedWebTokenRepository, RevokedWebTokenRepository>();
            services.AddScoped<ISecurityEventRepository, SecurityEventRepository>();
            services.AddScoped<IDeviceSyncProfileRepository, DeviceSyncProfileRepository>();
            services.AddScoped<IClientRequestReceiptRepository, ClientRequestReceiptRepository>();
            services.AddScoped<IFileChangeCursorRepository, FileChangeCursorRepository>();
            services.AddScoped<IFileChangeLogRepository, FileChangeLogRepository>();
            services.AddScoped<Core.Services.Sync.ISyncQueryService, Core.Services.Sync.SyncQueryService>();

            // -- Services (infrastructure-level) --
            services.AddScoped<IDepartmentPermissionService, DepartmentPermissionService>();
            services.AddSingleton<IPasswordService, PasswordService>();
            // NT hash encryption at rest (fails closed if NtHash:EncryptionKey is missing).
            services.AddSingleton<INtHashProtector, Security.AesGcmNtHashProtector>();
            services.AddScoped<IUserContextFactory, UserContextFactory>();
            if (readOnlyDemo)
            {
                // Wrap the real ACL service so every write-carrying permission is denied.
                services.AddScoped<AclService>();
                services.AddScoped<IAclService>(sp =>
                    new ReadOnlyDemoAclService(sp.GetRequiredService<AclService>()));
            }
            else
            {
                services.AddScoped<IAclService, AclService>();
            }
            services.AddScoped<IAuthenticationLookup, AuthenticationLookup>();
            services.AddScoped<IManagementAuthService, ManagementAuthService>();
            services.AddScoped<HomeDirectoryService>();
            services.AddSingleton<ICloudSyncPathUpdater, CloudSyncPathUpdater>();
            services.AddSingleton<ICloudSyncOperationCoordinator, DatabaseCloudSyncOperationCoordinator>();
            services.AddSingleton<IStorageConnectionCredentialLeaseManager, DatabaseStorageConnectionCredentialLeaseManager>();

            // -- Login: brute-force throttle (singleton, in-memory counters) +
            //    credential authentication with enumeration resistance --
            services.TryAddSingleton(TimeProvider.System);
            services.AddSingleton<ILoginThrottle, LoginThrottle>();
            services.AddScoped<ILoginService, CredentialLoginService>();

            // -- Search engine config flag (cross-process, read by the search router) --
            services.AddSingleton<ISearchConfigStore, Configuration.SearchConfigStore>();

            // -- SMB protocol settings (cross-process, read by the SMB host on start) --
            services.AddSingleton<ISmbConfigStore, Configuration.SmbConfigStore>();

            // -- Global log level (cross-process, read by the LoggingLevelReloader) --
            services.AddSingleton<Core.Logging.ILoggingConfigStore, Configuration.LoggingConfigStore>();

            // -- Database backup (pg_dump/pg_restore). Used by the Host (scheduled/
            //    pre-migration/restore) and the Web UI (manual create + download). --
            services.AddSingleton<Backup.IProcessRunner, Backup.ProcessRunner>();
            services.AddSingleton<Backup.IDatabaseBackupService, Backup.DatabaseBackupService>();
            services.AddSingleton<Backup.IBackupSettingsStore, Backup.BackupSettingsStore>();
            services.AddSingleton<Backup.BackupSchedulerSignal>();

            // -- Mail notifications: every process publishes events into the outbox table;
            //    only the Web process dispatches them (it alone can decrypt the SMTP password).
            //    The repository is stateless over the context factory, so singleton is safe. --
            services.AddSingleton<INotificationRepository, NotificationRepository>();
            services.AddSingleton<Notifications.NotificationDispatchSignal>();
            services.AddSingleton<Core.Services.Notifications.INotificationPublisher, Notifications.DbNotificationPublisher>();
            services.AddSingleton<Notifications.ISmtpConfigStore, Notifications.SmtpConfigStore>();
            services.AddSingleton<Notifications.MailTemplateRenderer>();
            services.AddScoped<Notifications.NotificationRecipientResolver>();

            // -- Seeder --
            services.AddScoped<DatabaseSeeder>();
            
            // -- Cloud --
            services.AddSingleton(MicrosoftIdentityConfiguration.FromConfiguration(configuration));
            services.AddSingleton(GoogleIdentityConfiguration.FromConfiguration(configuration));
            services.AddSingleton(DropboxIdentityConfiguration.FromConfiguration(configuration));
            services.AddSingleton<GoogleWorkspaceCredentialFactory>();
            services.AddSingleton<ICloudProvider, GoogleDriveProvider>();
            services.AddSingleton<ICloudProvider, OneDriveProvider>();
            services.AddSingleton<ICloudProvider, DropboxProvider>();
            services.AddSingleton<ICloudProviderFactory, CloudProviderFactory>();

            return services;
        }

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

        /// <summary>
        /// Registers external-storage protocol providers for the Web runtime.
        /// This is intentionally separate from <see cref="AddInfrastructure"/>
        /// because password-backed protocol providers depend on the Web-owned credential vault;
        /// the Host and SMB Bridge must not construct or validate them.
        /// </summary>
        public static IServiceCollection AddExternalStorageProviders(
            this IServiceCollection services,
            string applicationDataPath)
        {
            services.AddSingleton<IRsyncSshSetupService>(
                new RsyncSshSetupService(applicationDataPath));
            services.AddSingleton<IRsyncProcessRunner, RsyncProcessRunner>();
            services.AddSingleton<IProtocolCommandRunner, ProtocolCommandRunner>();
            services.AddScoped<IStorageConnectionProvider, SmbStorageConnectionProvider>();
            services.AddScoped<IStorageConnectionProvider, RsyncSshStorageConnectionProvider>();
            services.AddScoped<IStorageConnectionProvider, SftpStorageConnectionProvider>();
            services.AddScoped<IStorageConnectionProvider, WebDavStorageConnectionProvider>();
        services.AddScoped<IStorageConnectionProviderCatalog, StorageConnectionProviderCatalog>();
        services.AddScoped<IStorageDirectoryTargetResolver, StorageDirectoryTargetResolver>();
        return services;
        }

        /// <summary>
        /// Registers Core services. Live share I/O is always created from the
        /// absolute path persisted on the corresponding ShareDefinition. The
        /// application data path is used only for internal, non-share data.
        ///
        /// Call this once from every host AFTER <see cref="AddInfrastructure"/>.
        /// </summary>
        public static IServiceCollection AddCoreServices(
            this IServiceCollection services, IReadOnlyList<string> poolStoragePaths, string applicationDataPath)
        {
            // Disabled search
            services.TryAddSingleton<ISearchService, NoOpSearchService>();

            // -- System info (IP / first configured pool / RAM for settings) --
            services.AddSingleton<ISystemInfoService>(_ =>
                new SystemInfoService(poolStoragePaths));

            // -- ACL + Metadata --
            services.AddScoped<IAclRepository, AclRepository>();
            services.AddScoped<IFileMetadataRepository, FileMetadataRepository>();

            // -- File Service Factory (creates per-share FileService instances) --
            services.AddSingleton<IFileServiceFactory, FileServiceFactory>();

            // -- Root StorageEngine (share-agnostic, used by Web UI for raw I/O) --
            //services.AddSingleton<IStorageEngine>(sp =>
            //    new FileSystemStorage(storagePath, Guid.Empty, sp));
            VolumeMountManager.TrySetVolumeMounts(GetActiveStorageMounts());

            // -- Versioning (internal data, intentionally outside all shares) --
            // Blobs live in the pool of their share, never on the application-data
            // volume. The former app-data location is only read and drained
            // (VersionStorageReconcilerService in the Host).
            var legacyVersionStoragePath = Path.Combine(
                applicationDataPath, ".versions");

            services.AddScoped<IVersionStorageLocator>(sp =>
                new PoolVersionStorageLocator(
                    sp.GetRequiredService<IShareRepository>(), poolStoragePaths));

            services.AddScoped(sp =>
                new FileVersionService(
                    sp.GetRequiredService<IFileVersionRepository>(),
                    legacyVersionStoragePath,
                    defaultMaxVersions: 64,
                    defaultMaxAge: TimeSpan.FromDays(90),
                    logger: sp.GetRequiredService<ILogger<FileVersionService>>(),
                    storageLocator: sp.GetRequiredService<IVersionStorageLocator>()));
            services.AddScoped<IFileVersionService>(sp => sp.GetRequiredService<FileVersionService>());

            // -- Share Lock Manager (in-memory, single instance) --
            services.AddSingleton<ShareLockManager>();

            return services;
        }

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

        /// <summary>
        /// Restores a backup on startup when <c>Backup:RestoreFromPath</c> points
        /// at an existing dump that has not yet been restored (no <c>.done</c>
        /// marker). Returns <c>true</c> when a restore was performed. Runs under
        /// the caller's advisory lock, before migrations.
        /// </summary>
        private static async Task<bool> TryRestoreOnStartupAsync(IHost host, ILogger logger)
        {
            var configuration = host.Services.GetRequiredService<IConfiguration>();
            var requested = configuration["Backup:RestoreFromPath"];
            if (string.IsNullOrWhiteSpace(requested))
                return false;

            var backup = host.Services.GetRequiredService<IDatabaseBackupService>();

            // Accept whatever form the operator put in KAIMO_DB_RESTORE_FROM: an
            // absolute container path, a leftover host path, or just the file
            // name — with or without the ".dump" extension. Everything resolves
            // to a file inside the backup folder.
            var restorePath = ResolveRestoreTarget(requested, backup.BackupRootPath);
            if (restorePath is null)
            {
                // The operator explicitly asked for a restore, so do NOT boot
                // normally as if nothing was requested. Fail fast (before any
                // migration) with the list of backups that ARE present.
                var available = backup.ListBackups();
                var list = available.Count == 0
                    ? "(none found in the backup folder)"
                    : string.Join(", ", available.Select(b => b.FileName));
                throw new InvalidOperationException(
                    $"KAIMO_DB_RESTORE_FROM (Backup:RestoreFromPath) is set to '{requested}', but no matching " +
                    $"backup was found in the container's backup folder '{backup.BackupRootPath}'. Set it to the " +
                    $"file name of a backup that exists there (the '.dump' extension is optional). " +
                    $"Available backups: {list}.");
            }

            var markerPath = restorePath + ".done";
            if (File.Exists(markerPath))
            {
                logger.LogInformation(
                    "Startup restore for '{Path}' was already applied (marker present). Skipping.",
                    restorePath);
                return false;
            }

            logger.LogWarning(
                "Startup restore requested from '{Path}'. Restoring now — this OVERWRITES the current database.",
                restorePath);

            await backup.RestoreAsync(restorePath);

            try
            {
                await File.WriteAllTextAsync(
                    markerPath,
                    $"Restored at {DateTimeOffset.UtcNow:O}. Delete this marker to restore the same file again.");
            }
            catch (Exception ex)
            {
                // A missing marker would re-restore on the next boot. Fail loudly
                // rather than risk a restore loop.
                throw new InvalidOperationException(
                    $"Restore succeeded but the .done marker could not be written at '{markerPath}'. " +
                    "Refusing to continue to avoid restoring again on the next start.", ex);
            }

            logger.LogWarning("Startup restore from '{Path}' completed; wrote marker '{Marker}'.",
                restorePath, markerPath);
            return true;
        }

        /// <summary>
        /// Resolves the operator-supplied restore value to an existing dump file.
        /// Accepts the path exactly as given (absolute container path or relative
        /// to the working directory) or, more forgivingly, just the file name
        /// looked up inside <paramref name="backupRoot"/> — with or without the
        /// <c>.dump</c> extension, ignoring any stale host-path prefix. Returns
        /// <c>null</c> when nothing matches.
        /// </summary>
        internal static string? ResolveRestoreTarget(string requested, string backupRoot)
        {
            var value = requested.Trim();

            // 1) Exactly as given (correct absolute container path, or relative).
            if (File.Exists(value))
                return value;

            // 2) File name only, resolved against the backup folder. Using just
            //    the name means a leftover host-path prefix does not matter.
            var name = Path.GetFileName(value);
            if (string.IsNullOrEmpty(name))
                return null;

            var candidates = name.EndsWith(BackupFileNaming.Extension, StringComparison.Ordinal)
                ? new[] { name }
                : new[] { name + BackupFileNaming.Extension, name };

            foreach (var candidate in candidates)
            {
                var path = Path.Combine(backupRoot, candidate);
                if (File.Exists(path))
                    return path;
            }

            return null;
        }

        public static List<string> GetAllActiveMounts()
        {
            List<string> result = [];

            static IEnumerable<string> GetMountedPaths()
            {
                foreach (var line in File.ReadLines("/proc/mounts"))
                {
                    var parts = line.Split(' ');
                    if (parts.Length >= 2)
                        yield return Unescape(parts[1]); // Feld 2 = Mountpoint
                }
            }

            static string Unescape(string path) =>
                path.Replace("\\040", " ")
                    .Replace("\\011", "\t")
                    .Replace("\\012", "\n")
                    .Replace("\\134", "\\");

            result.AddRange(GetMountedPaths());

            return result;
        }

        public static List<string> GetActiveStorageMounts()
        {
            List<string> result = GetAllActiveMounts().Select(path => path).Where(path => path.StartsWith("/data/storage/")).ToList();
            return result;
        }
    }

    /// <summary>The session holding the migration lock was lost; startup retries the sequence.</summary>
    public sealed class MigrationLockLostException(string message) : Exception(message);

    /// <summary>The database schema is newer than this build; the process must not start.</summary>
    public sealed class SchemaVersionMismatchException(string message) : InvalidOperationException(message);
}
