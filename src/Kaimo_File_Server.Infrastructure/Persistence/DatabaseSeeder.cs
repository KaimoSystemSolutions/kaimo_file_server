using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Department;
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

            // -- Rollen mit ManagementPermissions --
            var requiredRoles = new (string Name, ManagementPermission Perms)[]
            {
                ("Administrator", ManagementPermission.FullAdmin),
                ("User", ManagementPermission.None),
                ("ShareManager", ManagementPermission.ShareAdmin),
                ("UserManager", ManagementPermission.UserAdmin | ManagementPermission.AssignGroups),
                ("DepartmentAdmin", ManagementPermission.DepartmentAdmin),
            };

            var existingRoles = await _db.Roles.ToListAsync();
            var existingRoleNames = existingRoles
                .Select(r => r.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var (roleName, perms) in requiredRoles)
            {
                if (existingRoleNames.Add(roleName))
                {
                    _db.Roles.Add(new Role(Guid.NewGuid(), roleName, perms, true));
                    Console.WriteLine($"[+] Rolle '{roleName}' angelegt (Permissions: {perms})");
                    changed = true;
                }
                else
                {
                    // Update existing role's ManagementPermissions if they were None
                    var existing = existingRoles.FirstOrDefault(
                        r => r.Name.Equals(roleName, StringComparison.OrdinalIgnoreCase));
                    if (existing != null && existing.ManagementPermissions == ManagementPermission.None
                        && perms != ManagementPermission.None)
                    {
                        existing.ManagementPermissions = perms;
                        Console.WriteLine($"[~] Rolle '{roleName}' ManagementPermissions aktualisiert → {perms}");
                        changed = true;
                    }
                }
            }

            // -- Gruppen --
            var requiredGroups = new[] { "Admins", "Developers", "Guests", "Everyone" };
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

            if (changed)
                await _db.SaveChangesAsync();

            // -- Departments --
            await SeedDepartmentsAsync();

            // -- Test-User (nur beim allerersten Start) --
            if (!_db.Users.Any())
            {
                await SeedTestUsersAsync();
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

        private async Task SeedDepartmentsAsync()
        {
            if (await _db.Departments.AnyAsync())
                return;

            var devDept = new Department("Entwicklung", "Software-Entwicklung");
            var marketingDept = new Department("Marketing", "Marketing & Kommunikation");

            _db.Departments.AddRange(devDept, marketingDept);
            await _db.SaveChangesAsync();

            Console.WriteLine("[+] Departments 'Entwicklung' und 'Marketing' angelegt");
        }

        private async Task SeedTestUsersAsync()
        {
            Console.WriteLine("[+] Erstelle Testbenutzer...");

            var adminRole = _db.Roles.First(r => r.Name == "Administrator");
            var userRole = _db.Roles.First(r => r.Name == "User");
            var shareManagerRole = _db.Roles.First(r => r.Name == "ShareManager");
            var userManagerRole = _db.Roles.First(r => r.Name == "UserManager");
            var deptAdminRole = _db.Roles.First(r => r.Name == "DepartmentAdmin");

            var adminGroup = _db.Groups.First(g => g.Name == "Admins");
            var guestGroup = _db.Groups.First(g => g.Name == "Guests");
            var devGroup = _db.Groups.First(g => g.Name == "Developers");
            var everyoneGroup = _db.Groups.First(g => g.Name == "Everyone");

            var devDept = _db.Departments.First(d => d.Name == "Entwicklung");

            var testShare = new ShareDefinition("test", "/data/storage/test");
            var projekteShare = new ShareDefinition("projekte", "/data/storage/projekte");
            var generalShare = new ShareDefinition("general", "/data/storage/general");
            _db.ShareDefinitions.AddRange(testShare, projekteShare, generalShare);

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

            // Gruppen-Zuweisungen
            _db.UserGroups.AddRange(
                new UserGroup(admin.Id, adminGroup.Id),
                new UserGroup(admin.Id, everyoneGroup.Id),
                new UserGroup(marco.Id, devGroup.Id),
                new UserGroup(marco.Id, everyoneGroup.Id),
                new UserGroup(guest.Id, guestGroup.Id),
                new UserGroup(guest.Id, everyoneGroup.Id));

            // Legacy UserRoles (für Rückwärtskompatibilität)
            _db.UserRoles.AddRange(
                new UserRole(admin.Id, adminRole.Id),
                new UserRole(admin.Id, shareManagerRole.Id),
                new UserRole(admin.Id, userManagerRole.Id),
                new UserRole(marco.Id, userRole.Id),
                new UserRole(guest.Id, userRole.Id));

            // Share-Zugriff
            _db.ShareAccessEntries.AddRange(
                new ShareAccessEntry("test", admin.Id),
                new ShareAccessEntry("test", devGroup.Id),
                new ShareAccessEntry("projekte", admin.Id),
                new ShareAccessEntry("projekte", marco.Id),
                new ShareAccessEntry("general", everyoneGroup.Id));

            // Department-Zuordnungen
            _db.DepartmentUsers.AddRange(
                new DepartmentUser(devDept.Id, marco.Id));

            _db.DepartmentGroups.AddRange(
                new DepartmentGroup(devDept.Id, devGroup.Id));

            _db.DepartmentShares.AddRange(
                new DepartmentShare(devDept.Id, projekteShare.Id),
                new DepartmentShare(devDept.Id, testShare.Id));

            await _db.SaveChangesAsync();

            // Scoped Role Assignments
            // Admin: global admin
            _db.ScopedRoleAssignments.Add(
                ScopedRoleAssignment.Global(admin.Id, adminRole.Id));

            // Marco: DepartmentAdmin für Entwicklung
            _db.ScopedRoleAssignments.Add(
                ScopedRoleAssignment.ForDepartment(marco.Id, deptAdminRole.Id, devDept.Id));

            await _db.SaveChangesAsync();

            Console.WriteLine($"[+] Testdaten erstellt:");
            Console.WriteLine($"    Admin:  admin / {adminPassword} (Global Admin)");
            Console.WriteLine($"    Marco:  marco.hanisch / {marcoPassword} (DepartmentAdmin → Entwicklung)");
            Console.WriteLine($"    Guest:  guest / (leer)");
        }

        /// <summary>
        /// Entfernt doppelte Rollen/Gruppen (gleicher Name) und migriert
        /// alle Zuweisungen auf den jeweils ältesten Eintrag.
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

                var affectedUserRoles = await _db.UserRoles
                    .Where(ur => removeIds.Contains(ur.RoleId))
                    .ToListAsync();

                foreach (var ur in affectedUserRoles)
                {
                    var exists = await _db.UserRoles
                        .AnyAsync(x => x.UserId == ur.UserId && x.RoleId == keep.Id);
                    if (!exists)
                        _db.UserRoles.Add(new UserRole(ur.UserId, keep.Id));
                    _db.UserRoles.Remove(ur);
                }

                // Also migrate scoped role assignments
                var affectedAssignments = await _db.ScopedRoleAssignments
                    .Where(a => removeIds.Contains(a.RoleId))
                    .ToListAsync();

                foreach (var a in affectedAssignments)
                {
                    var exists = await _db.ScopedRoleAssignments
                        .AnyAsync(x => x.PrincipalId == a.PrincipalId
                                    && x.RoleId == keep.Id
                                    && x.ScopeType == a.ScopeType
                                    && x.ScopeId == a.ScopeId);
                    if (!exists)
                    {
                        _db.ScopedRoleAssignments.Add(
                            new ScopedRoleAssignment(a.PrincipalId, keep.Id, a.ScopeType, a.ScopeId));
                    }
                    _db.ScopedRoleAssignments.Remove(a);
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

                // Also migrate department-group associations
                var affectedDeptGroups = await _db.DepartmentGroups
                    .Where(dg => removeIds.Contains(dg.GroupId))
                    .ToListAsync();

                foreach (var dg in affectedDeptGroups)
                {
                    var exists = await _db.DepartmentGroups
                        .AnyAsync(x => x.DepartmentId == dg.DepartmentId && x.GroupId == keep.Id);
                    if (!exists)
                        _db.DepartmentGroups.Add(new DepartmentGroup(dg.DepartmentId, keep.Id));
                    _db.DepartmentGroups.Remove(dg);
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