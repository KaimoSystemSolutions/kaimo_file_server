using Kaimo_File_Server_Core.Core.Domain;
using Kaimo_File_Server_Core.Core.Domain.Identity;
using Kaimo_File_Server_Core.Core.Security;
using System;
using System.Collections.Generic;
using System.Text;

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
            // Nur seeden wenn DB leer ist
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

            // Users
            var admin = new User(
                Guid.NewGuid(),
                "Administrator",
                "admin",
                _passwordService.HashPassword("admin1234"),
                _passwordService.ComputeNtHash("admin1234")
            );

            var marco = new User(
                Guid.NewGuid(),
                "Marco Hanisch",
                "marco.hanisch",
                _passwordService.HashPassword("1234"),
                _passwordService.ComputeNtHash("1234")
            );

            var guest = new User(
                Guid.NewGuid(),
                "Guest",
                "guest",
                _passwordService.HashPassword(""),
                _passwordService.ComputeNtHash("")
            );

            _db.Users.AddRange(admin, marco, guest);

            // Share-Zugriff (Whitelist)
            _db.ShareAccessEntries.AddRange(
                new ShareAccessEntry("test", admin.Id),
                //new ShareAccessEntry("test", marco.Id),
                new ShareAccessEntry("test", devGroup.Id),
                new ShareAccessEntry("projekte", admin.Id),
                new ShareAccessEntry("projekte", marco.Id)
            );

            

            await _db.SaveChangesAsync();
            Console.WriteLine($"[+] Testdaten erstellt:");
            Console.WriteLine($"    Admin: admin / admin123");
            Console.WriteLine($"    Marco: marco / test123");
            Console.WriteLine($"    Guest: guest / (leer)");
        }
    }
}
