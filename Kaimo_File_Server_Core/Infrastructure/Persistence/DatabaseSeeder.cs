using Kaimo_File_Server_Core.Core.Domain;
using Kaimo_File_Server_Core.Core.Domain.Identity;
using Kaimo_File_Server_Core.Core.Security;

namespace Kaimo_File_Server_Core.Infrastructure.Persistence
{
    public class DatabaseSeeder
    {
        private readonly ApplicationDbContext _db;
        private readonly IPasswordService _passwordService;

        public DatabaseSeeder(ApplicationDbContext db, IPasswordService passwordService)
        {
            _db = db;
            _passwordService = passwordService;
        }

        public async Task SeedAsync()
        {
            if (_db.Users.Any())
            {
                Console.WriteLine("[+] Testdaten bereits vorhanden");
                return;
            }

            Console.WriteLine("[+] Erstelle Testdaten...");

            // Gruppen
            var adminGroup = new Group(Guid.NewGuid(), "Admins");
            var devGroup = new Group(Guid.NewGuid(), "Developers");
            var guestGroup = new Group(Guid.NewGuid(), "Guests");
            _db.Groups.AddRange(adminGroup, devGroup, guestGroup);

            // Rollen
            var adminRole = new Role(Guid.NewGuid(), "Administrator");
            var userRole = new Role(Guid.NewGuid(), "User");
            _db.Roles.AddRange(adminRole, userRole);

            // Shares
            var testShare = new ShareDefinition("test", "/data/storage/test");
            var projekteShare = new ShareDefinition("projekte", "/data/storage/projekte");
            _db.ShareDefinitions.AddRange(testShare, projekteShare);

            // Users — Passwörter und Log-Output stimmen jetzt überein
            const string adminPassword = "admin1234";
            const string marcoPassword = "1234";
            const string guestPassword = "";

            var admin = new User(
                Guid.NewGuid(), "Administrator", "admin",
                _passwordService.HashPassword(adminPassword),
                _passwordService.ComputeNtHash(adminPassword)
            );

            var marco = new User(
                Guid.NewGuid(), "Marco Hanisch", "marco.hanisch",
                _passwordService.HashPassword(marcoPassword),
                _passwordService.ComputeNtHash(marcoPassword)
            );

            var guest = new User(
                Guid.NewGuid(), "Guest", "guest",
                _passwordService.HashPassword(guestPassword),
                _passwordService.ComputeNtHash(guestPassword)
            );

            _db.Users.AddRange(admin, marco, guest);

            // User -> Gruppen
            _db.UserGroups.AddRange(
                new UserGroup(admin.Id, adminGroup.Id),
                new UserGroup(guest.Id, guestGroup.Id)
            );

            // User -> Rollen
            _db.UserRoles.AddRange(
                new UserRole(admin.Id, adminRole.Id),
                new UserRole(marco.Id, userRole.Id),
                new UserRole(guest.Id, userRole.Id)
            );

            // Share-Zugriff
            _db.ShareAccessEntries.AddRange(
                new ShareAccessEntry("test", admin.Id),
                new ShareAccessEntry("test", devGroup.Id),
                new ShareAccessEntry("projekte", admin.Id),
                new ShareAccessEntry("projekte", marco.Id)
            );

            await _db.SaveChangesAsync();

            // Log-Output stimmt jetzt mit den tatsächlichen Passwörtern überein
            Console.WriteLine($"[+] Testdaten erstellt:");
            Console.WriteLine($"    Admin:  admin / {adminPassword}");
            Console.WriteLine($"    Marco:  marco.hanisch / {marcoPassword}");
            Console.WriteLine($"    Guest:  guest / (leer)");
        }
    }
}