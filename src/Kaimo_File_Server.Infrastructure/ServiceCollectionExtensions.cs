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
                ?? "Host=postgres;Database=kaimo_file_server_core;Username=kaimo_test_user;Password=change_me";

            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseNpgsql(connectionString));

            // Repositories
            services.AddScoped<IUserRepository, UserRepository>();
            services.AddScoped<IShareAccessRepository, ShareAccessRepository>();
            services.AddScoped<IShareRepository, ShareRepository>();

            // Services
            services.AddSingleton<IPasswordService, PasswordService>();
            services.AddScoped<IUserContextFactory, UserContextFactory>();
            services.AddScoped<DatabaseSeeder>();

            // AuthenticationLookup das Interface aus Core, die Implementierung aus Infrastructure
            services.AddScoped<IAuthenticationLookup, AuthenticationLookup>();

            // FileVersionRepositor
            services.AddScoped<IFileVersionRepository, FileVersionRepository>();

            return services;
        }

        /// <summary>
        /// Registers Core services (FileService, AclService, StorageEngine).
        /// Called from the Host project after AddInfrastructure.
        /// </summary>
        public static IServiceCollection AddCoreServices(this IServiceCollection services, string storagePath)
        {
            services.AddSingleton<IStorageEngine>(sp => new FileSystemStorage(storagePath, sp));
            services.AddSingleton<IAclService, AclService>();
            services.AddSingleton<IFileService, FileService>();

            var versionStoragePath = Path.Combine(storagePath, ".versions");

            // Use Scoped, not Singleton each version operation needs its own DbContext
            services.AddScoped<IFileVersionService>(sp =>
                new FileVersionService(
                    sp.GetRequiredService<IFileVersionRepository>(),
                    versionStoragePath,
                    defaultMaxVersions: 64,
                    defaultMaxAge: TimeSpan.FromDays(90)));

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
