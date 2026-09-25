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
            var connectionString = configuration.GetConnectionString("Default")
                ?? "Host=kaimo_file_server_db;Database=kaimo_file_server;Username=kaimo_test_user;Password=change_me";

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
                options.UseNpgsql(connectionString);
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

            // -- Seeder --
            services.AddScoped<DatabaseSeeder>();
            
            // -- Cloud --
            services.AddSingleton(MicrosoftIdentityConfiguration.FromConfiguration(configuration));
            services.AddSingleton(GoogleIdentityConfiguration.FromConfiguration(configuration));
            services.AddSingleton(DropboxIdentityConfiguration.FromConfiguration(configuration));
            services.AddSingleton<GoogleOAuthClientFactory>();
            services.AddSingleton<GoogleWorkspaceCredentialFactory>();
            services.AddSingleton<ICloudProvider, GoogleDriveProvider>();
            services.AddSingleton<ICloudProvider, OneDriveProvider>();
            services.AddSingleton<ICloudProvider, DropboxProvider>();
            services.AddSingleton<ICloudProviderFactory, CloudProviderFactory>();

            return services;
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
            var versionStoragePath = Path.Combine(
                applicationDataPath, ".versions");

            services.AddScoped<IFileVersionService>(sp =>
                new FileVersionService(
                    sp.GetRequiredService<IFileVersionRepository>(),
                    versionStoragePath,
                    defaultMaxVersions: 64,
                    defaultMaxAge: TimeSpan.FromDays(90),
                    logger: sp.GetRequiredService<ILogger<FileVersionService>>()));

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

                        // 1) One-shot startup restore (if requested via config),
                        //    before any migration. A .done marker prevents a repeat.
                        bool justRestored = await TryRestoreOnStartupAsync(host, logger);

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
                        await db.Database.MigrateAsync();
                        logger.LogInformation(LogEvents.DatabaseReady, LogMessages.DatabaseReady);

                        // Before the seeder stores the first NT hashes with the configured key.
                        Security.AesGcmNtHashProtector.ValidateKeyStrength(
                            host.Services.GetRequiredService<IConfiguration>()["NtHash:EncryptionKey"],
                            allowDevelopmentKey: host.Services.GetRequiredService<IHostEnvironment>().IsDevelopment(),
                            isFreshInstall: !await db.Users.AnyAsync(u => u.NtHash != ""),
                            logger);

                        var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
                        await seeder.SeedAsync();
                    }
                    finally
                    {
                        await db.Database.ExecuteSqlRawAsync(
                            "SELECT pg_advisory_unlock(hashtext('kaimo_file_server_migrations'))");
                        await db.Database.CloseConnectionAsync();
                    }

                    return;
                }
                // Only retry failures that Npgsql classifies as transient (for
                // example while PostgreSQL is still starting). Migration and
                // seeding errors must surface immediately with their true cause.
                catch (Npgsql.NpgsqlException ex) when (ex.IsTransient)
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
        /// Non-owner processes (Web, SmbBridge): wait until the Host has applied
        /// all migrations, then continue. Never migrates or seeds. Polls the
        /// migration state (no pending migrations) until the Host is done. This is
        /// the single, orchestration-independent readiness guarantee — it works
        /// regardless of Docker Compose ordering. Times out after 10 minutes.
        /// Then verifies this process holds the same NT-hash key as the stored
        /// data (<see cref="Security.NtHashKeyCanary"/>) and throws otherwise.
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
                    var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
                    if (pending.Count == 0)
                    {
                        logger.LogInformation(LogEvents.DatabaseReady, LogMessages.DatabaseReady);
                        break;
                    }

                    logger.LogInformation(
                        "Waiting for the database owner (Host) to finish migrations ({Count} pending).",
                        pending.Count);
                }
                catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException)
                {
                    logger.LogInformation(
                        "Waiting for the database to become reachable: {Message}", ex.Message);
                }

                if (DateTimeOffset.UtcNow > deadline)
                    throw new InvalidOperationException(
                        "Database was not ready (migrations still pending) after waiting. " +
                        "Ensure the Host process is running and able to migrate.");

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
}
