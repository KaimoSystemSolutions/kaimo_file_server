using Kaimo_File_Server.Core.Logging;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Storage;
using Kaimo_File_Server.Infrastructure.Clouds;
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

            // -- EF Core --
            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseNpgsql(connectionString));

            services.AddDbContextFactory<ApplicationDbContext>(options =>
                options.UseNpgsql(connectionString), ServiceLifetime.Scoped);

            // -- Repositories --
            services.AddScoped<IUserRepository, UserRepository>();
            services.AddScoped<IShareRepository, ShareRepository>();
            services.AddScoped<IGroupRepository, GroupRepository>();
            services.AddScoped<IRoleRepository, RoleRepository>();
            services.AddScoped<IDepartmentRepository, DepartmentRepository>();
            services.AddScoped<IScopedRoleAssignmentRepository, ScopedRoleAssignmentRepository>();
            services.AddScoped<IFileVersionRepository, FileVersionRepository>();

            // -- Services (infrastructure-level) --
            services.AddScoped<IDepartmentPermissionService, DepartmentPermissionService>();
            services.AddSingleton<IPasswordService, PasswordService>();
            // NT hash encryption at rest (fails closed if NtHash:EncryptionKey is missing).
            services.AddSingleton<INtHashProtector, Security.AesGcmNtHashProtector>();
            services.AddScoped<IUserContextFactory, UserContextFactory>();
            services.AddScoped<IAclService, AclService>();
            services.AddScoped<IAuthenticationLookup, AuthenticationLookup>();
            services.AddScoped<IManagementAuthService, ManagementAuthService>();

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

            // -- Seeder --
            services.AddScoped<DatabaseSeeder>();
            
            // -- Cloud --
            services.AddSingleton<ICloudProviderFactory, CloudProviderFactory>();
            
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
        /// Applies pending migrations / EnsureCreated and runs the seeder.
        /// Retries up to 5 times with a 3-second delay for cold-start scenarios
        /// (e.g. Docker Compose where the DB container isn't ready yet).
        /// </summary>
        public static async Task InitializeDatabaseAsync(this IHost host)
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

                    // Both the Host and the Web app call this on startup and each
                    // runs MigrateAsync() + the seeder against the same database.
                    // On a fresh DB they would otherwise race:
                    //   * migrations: both read an empty __EFMigrationsHistory and
                    //     apply the full chain concurrently, corrupting each other
                    //     (e.g. one process drops an index the other hasn't created
                    //     yet).
                    //   * seeding: the seeder guards with `if (Users.AnyAsync())
                    //     return;`, a check-then-insert that only holds if the two
                    //     processes are serialized — otherwise both insert the
                    //     'admin' account and hit a duplicate-key violation.
                    // A Postgres session-level advisory lock serializes them: the
                    // first process migrates + seeds, the second blocks here and
                    // then finds migrations already applied (no-op) and rows
                    // already present (seeder skips).
                    //
                    // The connection is opened explicitly so it stays the same
                    // physical session for the whole critical section — EF does not
                    // close a connection it did not open, so the advisory lock is
                    // held across MigrateAsync() and the seeder (which shares this
                    // scoped DbContext / connection).
                    await db.Database.OpenConnectionAsync();
                    try
                    {
                        await db.Database.ExecuteSqlRawAsync(
                            "SELECT pg_advisory_lock(hashtext('kaimo_file_server_migrations'))");

                        //await db.Database.EnsureCreatedAsync();
                        await db.Database.MigrateAsync();
                        logger.LogInformation(LogEvents.DatabaseReady, LogMessages.DatabaseReady);

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
                catch (Exception ex)
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