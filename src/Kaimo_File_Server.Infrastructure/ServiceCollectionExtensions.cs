using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Storage;
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
            services.AddScoped<IUserContextFactory, UserContextFactory>();
            services.AddScoped<IAclService, AclService>();
            services.AddScoped<IAuthenticationLookup, AuthenticationLookup>();
            services.AddScoped<IManagementAuthService, ManagementAuthService>();

            // -- Seeder --
            services.AddScoped<DatabaseSeeder>();

            return services;
        }

        /// <summary>
        /// Registers Core services that depend on a storage root path:
        /// ACL repository, file metadata repository, file service factory,
        /// root storage engine, file versioning, and the share lock manager.
        ///
        /// Call this once from every host AFTER <see cref="AddInfrastructure"/>.
        /// </summary>
        public static IServiceCollection AddCoreServices(
            this IServiceCollection services, string storagePath)
        {
            // Disabled search
            services.TryAddSingleton<ISearchService, NoOpSearchService>();

            // -- System info (IP / storage / RAM for the settings page) --
            services.AddSingleton<ISystemInfoService>(_ => new SystemInfoService(storagePath));

            // -- ACL + Metadata --
            services.AddScoped<IAclRepository, AclRepository>();
            services.AddScoped<IFileMetadataRepository, FileMetadataRepository>();

            // -- File Service Factory (creates per-share FileService instances) --
            services.AddSingleton<IFileServiceFactory, FileServiceFactory>();

            // -- Root StorageEngine (share-agnostic, used by Web UI for raw I/O) --
            services.AddSingleton<IStorageEngine>(sp =>
                new FileSystemStorage(storagePath, Guid.Empty, sp));

            // -- Versioning --
            var versionStoragePath = Path.Combine(storagePath, ".versions");

            services.AddScoped<IFileVersionService>(sp =>
                new FileVersionService(
                    sp.GetRequiredService<IFileVersionRepository>(),
                    versionStoragePath,
                    defaultMaxVersions: 64,
                    defaultMaxAge: TimeSpan.FromDays(90)));

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

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    using var scope = host.Services.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    //await db.Database.EnsureCreatedAsync();
                    await db.Database.MigrateAsync();
                    Console.WriteLine("[+] Datenbank bereit");

                    var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
                    await seeder.SeedAsync();
                    return;
                }
                catch (Exception ex)
                {
                    if (attempt == maxRetries)
                        throw new InvalidOperationException(
                            "Datenbank konnte nach mehreren Versuchen nicht erreicht werden.", ex);

                    Console.WriteLine(
                        $"[!] DB nicht bereit, warte 3s... ({maxRetries - attempt} Versuche übrig)");
                    await Task.Delay(3000);
                }
            }
        }
    }
}