using Kaimo_File_Server_Core.Core.Repositories;
using Kaimo_File_Server_Core.Core.Security;
using Kaimo_File_Server_Core.Infrastructure.Persistence;
using Kaimo_File_Server_Core.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server_Core.Infrastructure
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
        {
            var connectionString = configuration.GetConnectionString("Default")
                ?? "Host=postgres;Database=kaimo_file_server_core;Username=kaimo_test_user;Password=change_me";

            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseNpgsql(connectionString));

            // Repositories — jedes nur einmal
            services.AddScoped<IUserRepository, UserRepository>();
            services.AddScoped<IShareAccessRepository, ShareAccessRepository>();
            services.AddScoped<IShareRepository, ShareRepository>();

            // Infrastruktur-Services
            services.AddSingleton<IPasswordService, PasswordService>();
            services.AddScoped<DatabaseSeeder>();

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