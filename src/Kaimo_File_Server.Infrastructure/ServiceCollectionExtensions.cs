using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Infrastructure.Clouds;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Infrastructure.ExternalStorage;
using Kaimo_File_Server.Infrastructure.Persistence;
using Kaimo_File_Server.Infrastructure.Repositories;
using Kaimo_File_Server.Infrastructure.Services;
using Kaimo_File_Server.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure
{
    public static partial class ServiceCollectionExtensions
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
            services.AddScoped<AccessRestrictionService>();
            services.AddScoped<ILoginService, CredentialLoginService>();

            // -- Search engine config flag (cross-process, read by the search router) --
            services.AddSingleton<ISearchConfigStore, Configuration.SearchConfigStore>();
            // -- Local (PostgreSQL) search index; stateless over the context factory --
            services.AddSingleton<ISearchIndexRepository, SearchIndexRepository>();

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
            services.AddSingleton<PoolUsageService>();

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

            // The global versioning policy (settings page) decides what is versioned and how
            // long; every process holds its own refreshing copy.
            services.AddSingleton<IVersioningPolicyStore, VersioningPolicyStore>();
            services.AddSingleton<IVersioningPolicyProvider>(sp => sp.GetRequiredService<IVersioningPolicyStore>());

            services.AddScoped(sp =>
                new FileVersionService(
                    sp.GetRequiredService<IFileVersionRepository>(),
                    legacyVersionStoragePath,
                    logger: sp.GetRequiredService<ILogger<FileVersionService>>(),
                    storageLocator: sp.GetRequiredService<IVersionStorageLocator>(),
                    policyProvider: sp.GetRequiredService<IVersioningPolicyProvider>()));
            services.AddScoped<IFileVersionService>(sp => sp.GetRequiredService<FileVersionService>());
            // Policy retention for unchanged files, orphan cleanup and pool budgets (Host job).
            services.AddScoped(sp => new VersionRetentionSweeper(
                sp.GetRequiredService<IFileVersionRepository>(),
                sp.GetRequiredService<FileVersionService>(),
                sp.GetRequiredService<IShareRepository>(),
                sp.GetRequiredService<IVersioningPolicyProvider>(),
                sp.GetRequiredService<IVersionStorageLocator>(),
                sp.GetRequiredService<ILogger<VersionRetentionSweeper>>(),
                sp.GetRequiredService<TimeProvider>()));

            // -- Share Lock Manager (in-memory, single instance) --
            services.AddSingleton<ShareLockManager>();

            return services;
        }
    }
}
