using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Microsoft.EntityFrameworkCore;

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
            // -- Einmalig: vorhandene Duplikate bereinigen --
            await RemoveDuplicatesAsync();

            var changed = false;

            // -- Rollen (by Name, da systemdefiniert) --
            var requiredRoles = new[] { "Administrator", "User", "ShareManager", "UserManager" };
            var existingRoleNames = (await _db.Roles.Select(r => r.Name).ToListAsync())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var roleName in requiredRoles)
            {
                if (existingRoleNames.Add(roleName))
                {
                    _db.Roles.Add(new Role(Guid.NewGuid(), roleName));
                    Console.WriteLine($"[+] Rolle '{roleName}' angelegt");
                    changed = true;
                }
            }

            // -- Gruppen --
            var requiredGroups = new[] { "Admins", "Developers", "Guests" };
            var existingGroupNames = (await _db.Groups.Select(g => g.Name).ToListAsync())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var groupName in requiredGroups)
            {
                if (existingGroupNames.Add(groupName))
                {
                    _db.Groups.Add(new Group(Guid.NewGuid(), groupName));
                    Console.WriteLine($"[+] Gruppe '{groupName}' angelegt");
                    changed = true;
                }
            }

            // Zwischenspeichern damit wir die IDs für Zuweisungen haben
            if (changed)
                await _db.SaveChangesAsync();

            // -- Test-User (nur beim allerersten Start) --
            if (!_db.Users.Any())
            {
                Console.WriteLine("[+] Erstelle Testbenutzer...");

                var adminRole = _db.Roles.First(r => r.Name == "Administrator");
                var userRole = _db.Roles.First(r => r.Name == "User");
                var ShareManagerRole = _db.Roles.First(r => r.Name == "ShareManager");
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
                    _passwordService.ComputeNtHash(adminPassword),
                    description: "Built-in administrator account",
                    email: "admin@kaimo.local",
                    isEnabled: true,
                    canChangePassword: true);

                var marco = new User(Guid.NewGuid(), "Marco Hanisch", "marco.hanisch",
                    _passwordService.HashPassword(marcoPassword),
                    _passwordService.ComputeNtHash(marcoPassword),
                    description: "",
                    email: "marco.hanisch@kaimo.local",
                    isEnabled: true,
                    canChangePassword: true);

                var guest = new User(Guid.NewGuid(), "Guest", "guest",
                    _passwordService.HashPassword(guestPassword),
                    _passwordService.ComputeNtHash(guestPassword),
                    description: "Built-in guest account",
                    email: "",
                    isEnabled: true,
                    canChangePassword: false);

                _db.Users.AddRange(admin, marco, guest);

                _db.UserGroups.AddRange(
                    new UserGroup(admin.Id, adminGroup.Id),
                    new UserGroup(guest.Id, guestGroup.Id));

                _db.UserRoles.AddRange(
                    new UserRole(admin.Id, adminRole.Id),
                    new UserRole(admin.Id, ShareManagerRole.Id),
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

        /// <summary>
        /// Entfernt doppelte Rollen/Gruppen (gleicher Name) und migriert
        /// alle Zuweisungen (UserRoles/UserGroups/ShareAccess) auf den
        /// jeweils ältesten Eintrag (= kleinste Id).
        /// </summary>
        private async Task RemoveDuplicatesAsync()
        {
            var cleaned = false;

            // -- Doppelte Rollen --
            var allRoles = await _db.Roles.ToListAsync();
            var roleDupes = allRoles
                .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1);

            foreach (var group in roleDupes)
            {
                var keep = group.OrderBy(r => r.Id).First();
                var remove = group.Where(r => r.Id != keep.Id).ToList();
                var removeIds = remove.Select(r => r.Id).ToHashSet();

                // UserRoles umhängen
                var affectedUserRoles = await _db.UserRoles
                    .Where(ur => removeIds.Contains(ur.RoleId))
                    .ToListAsync();

                foreach (var ur in affectedUserRoles)
                {
                    // Nur umhängen wenn die Kombination nicht schon existiert
                    var exists = await _db.UserRoles
                        .AnyAsync(x => x.UserId == ur.UserId && x.RoleId == keep.Id);
                    if (!exists)
                        _db.UserRoles.Add(new UserRole(ur.UserId, keep.Id));
                    _db.UserRoles.Remove(ur);
                }

                _db.Roles.RemoveRange(remove);
                Console.WriteLine($"[~] {remove.Count} doppelte Rolle(n) '{group.Key}' entfernt");
                cleaned = true;
            }

            // -- Doppelte Gruppen --
            var allGroups = await _db.Groups.ToListAsync();
            var groupDupes = allGroups
                .GroupBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1);

            foreach (var group in groupDupes)
            {
                var keep = group.OrderBy(g => g.Id).First();
                var remove = group.Where(g => g.Id != keep.Id).ToList();
                var removeIds = remove.Select(g => g.Id).ToHashSet();

                // UserGroups umhängen
                var affectedUserGroups = await _db.UserGroups
                    .Where(ug => removeIds.Contains(ug.GroupId))
                    .ToListAsync();

                foreach (var ug in affectedUserGroups)
                {
                    var exists = await _db.UserGroups
                        .AnyAsync(x => x.UserId == ug.UserId && x.GroupId == keep.Id);
                    if (!exists)
                        _db.UserGroups.Add(new UserGroup(ug.UserId, keep.Id));
                    _db.UserGroups.Remove(ug);
                }

                // ShareAccessEntries umhängen (PrincipalId kann Gruppen-Id sein)
                var affectedShares = await _db.ShareAccessEntries
                    .Where(sa => removeIds.Contains(sa.PrincipalId))
                    .ToListAsync();

                foreach (var sa in affectedShares)
                {
                    var exists = await _db.ShareAccessEntries
                        .AnyAsync(x => x.ShareName == sa.ShareName && x.PrincipalId == keep.Id);
                    if (!exists)
                        _db.ShareAccessEntries.Add(new ShareAccessEntry(sa.ShareName, keep.Id));
                    _db.ShareAccessEntries.Remove(sa);
                }

                _db.Groups.RemoveRange(remove);
                Console.WriteLine($"[~] {remove.Count} doppelte Gruppe(n) '{group.Key}' entfernt");
                cleaned = true;
            }

            if (cleaned)
                await _db.SaveChangesAsync();
        }

    }
}