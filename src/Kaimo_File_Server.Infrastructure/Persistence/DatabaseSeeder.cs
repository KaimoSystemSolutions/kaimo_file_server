using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;

namespace Kaimo_File_Server.Infrastructure.Persistence
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
            var changed = false;

            // ── Rollen (by Name, da systemdefiniert) ──
            var requiredRoles = new[] { "Administrator", "User", "ShareCreator", "UserManager" };
            var existingRoleNames = _db.Roles.Select(r => r.Name).ToHashSet();
            foreach (var roleName in requiredRoles)
            {
                if (!existingRoleNames.Contains(roleName))
                {
                    _db.Roles.Add(new Role(Guid.NewGuid(), roleName));
                    Console.WriteLine($"[+] Rolle '{roleName}' angelegt");
                    changed = true;
                }
            }

            // ── Gruppen ──
            var requiredGroups = new[] { "Admins", "Developers", "Guests" };
            var existingGroupNames = _db.Groups.Select(g => g.Name).ToHashSet();
            foreach (var groupName in requiredGroups)
            {
                if (!existingGroupNames.Contains(groupName))
                {
                    _db.Groups.Add(new Group(Guid.NewGuid(), groupName));
                    Console.WriteLine($"[+] Gruppe '{groupName}' angelegt");
                    changed = true;
                }
            }

            // Zwischenspeichern damit wir die IDs für Zuweisungen haben
            if (changed)
                await _db.SaveChangesAsync();

            // ── Test-User (nur beim allerersten Start) ──
            if (!_db.Users.Any())
            {
                Console.WriteLine("[+] Erstelle Testbenutzer...");

                var adminRole = _db.Roles.First(r => r.Name == "Administrator");
                var userRole = _db.Roles.First(r => r.Name == "User");
                var shareCreatorRole = _db.Roles.First(r => r.Name == "ShareCreator");
                var userManagerRole = _db.Roles.First(r => r.Name == "UserManager");

                var adminGroup = _db.Groups.First(g => g.Name == "Admins");
                var guestGroup = _db.Groups.First(g => g.Name == "Guests");
                var devGroup = _db.Groups.First(g => g.Name == "Developers");

                var testShare = new ShareDefinition("test", "/data/storage/test");
                var projekteShare = new ShareDefinition("projekte", "/data/storage/projekte");
                _db.ShareDefinitions.AddRange(testShare, projekteShare);

                const string adminPassword = "admin1234";
                const string marcoPassword = "1234";
                const string guestPassword = "";

                var admin = new User(Guid.NewGuid(), "Administrator", "admin",
                    _passwordService.HashPassword(adminPassword),
                    _passwordService.ComputeNtHash(adminPassword));

                var marco = new User(Guid.NewGuid(), "Marco Hanisch", "marco.hanisch",
                    _passwordService.HashPassword(marcoPassword),
                    _passwordService.ComputeNtHash(marcoPassword));

                var guest = new User(Guid.NewGuid(), "Guest", "guest",
                    _passwordService.HashPassword(guestPassword),
                    _passwordService.ComputeNtHash(guestPassword));

                _db.Users.AddRange(admin, marco, guest);

                _db.UserGroups.AddRange(
                    new UserGroup(admin.Id, adminGroup.Id),
                    new UserGroup(guest.Id, guestGroup.Id));

                _db.UserRoles.AddRange(
                    new UserRole(admin.Id, adminRole.Id),
                    new UserRole(admin.Id, shareCreatorRole.Id),
                    new UserRole(admin.Id, userManagerRole.Id),
                    new UserRole(marco.Id, userRole.Id),
                    new UserRole(guest.Id, userRole.Id));

                _db.ShareAccessEntries.AddRange(
                    new ShareAccessEntry("test", admin.Id),
                    new ShareAccessEntry("test", devGroup.Id),
                    new ShareAccessEntry("projekte", admin.Id),
                    new ShareAccessEntry("projekte", marco.Id));

                await _db.SaveChangesAsync();

                Console.WriteLine($"[+] Testdaten erstellt:");
                Console.WriteLine($"    Admin:  admin / {adminPassword}");
                Console.WriteLine($"    Marco:  marco.hanisch / {marcoPassword}");
                Console.WriteLine($"    Guest:  guest / (leer)");
            }
            else if (changed)
            {
                Console.WriteLine("[+] Fehlende Rollen/Gruppen nachgetragen");
            }
            else
            {
                Console.WriteLine("[+] Testdaten bereits vorhanden");
            }
        }

    }
}
