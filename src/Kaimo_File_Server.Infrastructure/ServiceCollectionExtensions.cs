using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Storage;
using Kaimo_File_Server.Infrastructure.Persistence;
using Kaimo_File_Server.Infrastructure.Repositories;
using Kaimo_File_Server.Infrastructure.Services;
using Kaimo_File_Server.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kaimo_File_Server.Infrastructure
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
        {
            var connectionString = configuration.GetConnectionString("Default")
                ?? "Host=kaimo_file_server_db;Database=kaimo_file_server;Username=kaimo_test_user;Password=change_me";

            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseNpgsql(connectionString));

            // Repositories
            services.AddScoped<IUserRepository, UserRepository>();
            services.AddScoped<IShareAccessRepository, ShareAccessRepository>();
            services.AddScoped<IShareRepository, ShareRepository>();
            services.AddScoped<IGroupRepository, GroupRepository>();
            services.AddScoped<IRoleRepository, RoleRepository>();

            // Services
            services.AddSingleton<IPasswordService, PasswordService>();
            services.AddScoped<IUserContextFactory, UserContextFactory>();
            services.AddScoped<DatabaseSeeder>();

            // AuthenticationLookup das Interface aus Core, die Implementierung aus Infrastructure
            services.AddScoped<IAuthenticationLookup, AuthenticationLookup>();

            // FileVersionRepository
            services.AddScoped<IFileVersionRepository, FileVersionRepository>();

            return services;
        }

        /// <summary>
        /// Registers Core services (FileService, AclService, StorageEngine).
        /// Called from the Host project after AddInfrastructure..
        /// </summary>
        public static IServiceCollection AddCoreServices(this IServiceCollection services, string storagePath)
        {

            services.AddScoped<IAclRepository, Kaimo_File_Server.Infrastructure.Repositories.AclRepository>();
            services.AddScoped<IFileMetadataRepository, Kaimo_File_Server.Infrastructure.Repositories.FileMetadataRepository>();

            // Factory für share-spezifische FileService-Instanzen (mit ACL)
            services.AddSingleton<IFileServiceFactory, FileServiceFactory>();

            // Root-StorageEngine für die Web-UI (Listing, Ordner erstellen — ohne ACL)
            services.AddSingleton<IStorageEngine>(sp =>
                new FileSystemStorage(storagePath, Guid.Empty, sp));

            var versionStoragePath = Path.Combine(storagePath, ".versions");

            services.AddScoped<IFileVersionService>(sp =>
                new FileVersionService(
                    sp.GetRequiredService<IFileVersionRepository>(),
                    versionStoragePath,
                    defaultMaxVersions: 64,
                    defaultMaxAge: TimeSpan.FromDays(90)));

            services.AddSingleton<ShareLockManager>();

            return services;
        }

        public static async Task InitializeDatabaseAsync(this IHost host)
        {
            var retries = 5;
            while (retries > 0)
            {
                try
                {
                    using var scope = host.Services.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    await db.Database.EnsureCreatedAsync();
                    Console.WriteLine("[+] Datenbank bereit");

                    var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
                    await seeder.SeedAsync();
                    return;
                }
                catch
                {
                    retries--;
                    Console.WriteLine($"[!] DB nicht bereit, warte 3s... ({retries} Versuche übrig)");
                    await Task.Delay(3000);
                }
            }

            throw new Exception("Datenbank konnte nicht erreicht werden");
        }


    }

}