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
            await RemoveDuplicatesAsync();

            var changed = false;

            // ── Rollen mit ManagementPermissions ──
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

            // ── Gruppen ──
            var requiredGroups = new[] { "Admins", "Developers", "Guests", "Everyone", "Backend-Team", "Frontend-Team", "Marketing-Team" };
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

            // ── Departments (mit Hierarchie) ──
            await SeedDepartmentsAsync();

            // ── Test-User (nur beim allerersten Start) ──
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

        /// <summary>
        /// Seeds departments with a hierarchy that demonstrates OOP-style inheritance:
        ///
        ///   Entwicklung (DefaultFilePermission = Read|Write)
        ///   ├── Backend    (null → erbt Read|Write von Entwicklung)
        ///   └── Frontend   (null → erbt Read|Write von Entwicklung)
        ///
        ///   Marketing (DefaultFilePermission = Read)
        ///
        ///   Geschäftsleitung (DefaultFilePermission = Read|Write|Delete)
        ///
        /// Admin scoped to "Entwicklung" can also manage Backend + Frontend (descendants).
        /// </summary>
        private async Task SeedDepartmentsAsync()
        {
            if (await _db.Departments.AnyAsync())
                return;

            // FilePermission flags — adjust to match your actual FilePermission enum
            const long Read = 1L;
            const long Write = 2L;
            const long Delete = 4L;

            // Top-level departments
            var entwicklung = new Department("Entwicklung", "Software-Entwicklung")
            {
                DefaultFilePermission = Read | Write
            };

            var marketing = new Department("Marketing", "Marketing & Kommunikation")
            {
                DefaultFilePermission = Read
            };

            var geschaeftsl = new Department("Geschäftsleitung", "Unternehmensführung")
            {
                DefaultFilePermission = Read | Write | Delete
            };

            _db.Departments.AddRange(entwicklung, marketing, geschaeftsl);
            await _db.SaveChangesAsync();

            // Child departments (inherit from Entwicklung)
            var backend = new Department("Backend", "Backend-Entwicklung", entwicklung.Id);
            var frontend = new Department("Frontend", "Frontend-Entwicklung", entwicklung.Id);

            _db.Departments.AddRange(backend, frontend);
            await _db.SaveChangesAsync();

            Console.WriteLine("[+] Departments angelegt:");
            Console.WriteLine($"    Entwicklung (Default: Read|Write)");
            Console.WriteLine($"    ├── Backend (erbt von Entwicklung)");
            Console.WriteLine($"    └── Frontend (erbt von Entwicklung)");
            Console.WriteLine($"    Marketing (Default: Read)");
            Console.WriteLine($"    Geschäftsleitung (Default: Read|Write|Delete)");
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
            var backendTeam = _db.Groups.First(g => g.Name == "Backend-Team");
            var frontendTeam = _db.Groups.First(g => g.Name == "Frontend-Team");
            var marketingTeam = _db.Groups.First(g => g.Name == "Marketing-Team");

            var devDept = _db.Departments.First(d => d.Name == "Entwicklung");
            var backendDept = _db.Departments.First(d => d.Name == "Backend");
            var frontendDept = _db.Departments.First(d => d.Name == "Frontend");
            var marketingDept = _db.Departments.First(d => d.Name == "Marketing");
            var geschaeftsDept = _db.Departments.First(d => d.Name == "Geschäftsleitung");

            // ── Shares ──
            var testShare = new ShareDefinition("test", "/data/storage/test");
            var projekteShare = new ShareDefinition("projekte", "/data/storage/projekte");
            var generalShare = new ShareDefinition("general", "/data/storage/general");
            var backendShare = new ShareDefinition("backend-docs", "/data/storage/backend-docs");
            var frontendShare = new ShareDefinition("frontend-docs", "/data/storage/frontend-docs");
            var marketingShare = new ShareDefinition("marketing-files", "/data/storage/marketing-files");
            _db.ShareDefinitions.AddRange(testShare, projekteShare, generalShare,
                backendShare, frontendShare, marketingShare);

            // ── Users ──
            const string adminPassword = "admin1234";
            const string marcoPassword = "1234";
            const string annaPassword = "1234";
            const string lisaPassword = "1234";
            const string guestPassword = "";

            var admin = new User(Guid.NewGuid(), "Administrator", "admin",
                _passwordService.HashPassword(adminPassword),
                _passwordService.ComputeNtHash(adminPassword),
                description: "Built-in administrator account",
                email: "admin@kaimo.local",
                isEnabled: true, canChangePassword: true);

            var marco = new User(Guid.NewGuid(), "Marco Hanisch", "marco.hanisch",
                _passwordService.HashPassword(marcoPassword),
                _passwordService.ComputeNtHash(marcoPassword),
                description: "Abteilungsleiter Entwicklung",
                email: "marco.hanisch@kaimo.local",
                isEnabled: true, canChangePassword: true);

            var anna = new User(Guid.NewGuid(), "Anna Weber", "anna.weber",
                _passwordService.HashPassword(annaPassword),
                _passwordService.ComputeNtHash(annaPassword),
                description: "Backend-Entwicklerin",
                email: "anna.weber@kaimo.local",
                isEnabled: true, canChangePassword: true);

            var lisa = new User(Guid.NewGuid(), "Lisa Müller", "lisa.mueller",
                _passwordService.HashPassword(lisaPassword),
                _passwordService.ComputeNtHash(lisaPassword),
                description: "Marketing-Leiterin",
                email: "lisa.mueller@kaimo.local",
                isEnabled: true, canChangePassword: true);

            var guest = new User(Guid.NewGuid(), "Guest", "guest",
                _passwordService.HashPassword(guestPassword),
                _passwordService.ComputeNtHash(guestPassword),
                description: "Built-in guest account",
                email: "",
                isEnabled: true, canChangePassword: false);

            _db.Users.AddRange(admin, marco, anna, lisa, guest);

            // ── Gruppen-Zuweisungen ──
            _db.UserGroups.AddRange(
                new UserGroup(admin.Id, adminGroup.Id),
                new UserGroup(admin.Id, everyoneGroup.Id),
                new UserGroup(marco.Id, devGroup.Id),
                new UserGroup(marco.Id, everyoneGroup.Id),
                new UserGroup(anna.Id, backendTeam.Id),
                new UserGroup(anna.Id, devGroup.Id),
                new UserGroup(anna.Id, everyoneGroup.Id),
                new UserGroup(lisa.Id, marketingTeam.Id),
                new UserGroup(lisa.Id, everyoneGroup.Id),
                new UserGroup(guest.Id, guestGroup.Id),
                new UserGroup(guest.Id, everyoneGroup.Id));

            // ── Legacy UserRoles ──
            _db.UserRoles.AddRange(
                new UserRole(admin.Id, adminRole.Id),
                new UserRole(admin.Id, shareManagerRole.Id),
                new UserRole(admin.Id, userManagerRole.Id),
                new UserRole(marco.Id, userRole.Id),
                new UserRole(anna.Id, userRole.Id),
                new UserRole(lisa.Id, userRole.Id),
                new UserRole(guest.Id, userRole.Id));

            // ── Share-Zugriff ──
            _db.ShareAccessEntries.AddRange(
                new ShareAccessEntry("test", admin.Id),
                new ShareAccessEntry("test", devGroup.Id),
                new ShareAccessEntry("projekte", admin.Id),
                new ShareAccessEntry("projekte", marco.Id),
                new ShareAccessEntry("general", everyoneGroup.Id),
                new ShareAccessEntry("backend-docs", backendTeam.Id),
                new ShareAccessEntry("frontend-docs", frontendTeam.Id),
                new ShareAccessEntry("marketing-files", marketingTeam.Id));

            // ── Department → User Zuordnungen ──
            _db.DepartmentUsers.AddRange(
                // Marco: Abteilungsleiter Entwicklung (top-level)
                new DepartmentUser(devDept.Id, marco.Id),
                // Anna: Backend-Abteilung (Kind von Entwicklung)
                new DepartmentUser(backendDept.Id, anna.Id),
                // Lisa: Marketing
                new DepartmentUser(marketingDept.Id, lisa.Id));

            // ── Department → Group Zuordnungen ──
            _db.DepartmentGroups.AddRange(
                new DepartmentGroup(devDept.Id, devGroup.Id),
                new DepartmentGroup(backendDept.Id, backendTeam.Id),
                new DepartmentGroup(frontendDept.Id, frontendTeam.Id),
                new DepartmentGroup(marketingDept.Id, marketingTeam.Id));

            // ── Department → Share Zuordnungen ──
            _db.DepartmentShares.AddRange(
                // Entwicklung-Level Shares (gelten auch für Backend/Frontend via Hierarchie)
                new DepartmentShare(devDept.Id, projekteShare.Id),
                new DepartmentShare(devDept.Id, testShare.Id),
                // Backend-spezifischer Share
                new DepartmentShare(backendDept.Id, backendShare.Id),
                // Frontend-spezifischer Share
                new DepartmentShare(frontendDept.Id, frontendShare.Id),
                // Marketing Share
                new DepartmentShare(marketingDept.Id, marketingShare.Id),
                // General ist für alle (keiner Abteilung exklusiv, aber über ShareAccess → Everyone)
                new DepartmentShare(geschaeftsDept.Id, generalShare.Id));

            await _db.SaveChangesAsync();

            // ── Scoped Role Assignments ──

            // Admin: globaler Administrator
            _db.ScopedRoleAssignments.Add(
                ScopedRoleAssignment.Global(admin.Id, adminRole.Id));

            // Marco: DepartmentAdmin für Entwicklung
            // → kann auch Backend + Frontend verwalten (Hierarchie-Vererbung)
            _db.ScopedRoleAssignments.Add(
                ScopedRoleAssignment.ForDepartment(marco.Id, deptAdminRole.Id, devDept.Id));

            // Lisa: DepartmentAdmin für Marketing
            _db.ScopedRoleAssignments.Add(
                ScopedRoleAssignment.ForDepartment(lisa.Id, deptAdminRole.Id, marketingDept.Id));

            // Backend-Team Gruppe: ShareManager für backend-docs Share
            _db.ScopedRoleAssignments.Add(
                ScopedRoleAssignment.ForShare(backendTeam.Id, shareManagerRole.Id, backendShare.Id));

            await _db.SaveChangesAsync();

            Console.WriteLine("[+] Testdaten erstellt:");
            Console.WriteLine($"    Admin:  admin / {adminPassword}");
            Console.WriteLine($"      → Global Admin");
            Console.WriteLine($"    Marco:  marco.hanisch / {marcoPassword}");
            Console.WriteLine($"      → DepartmentAdmin für Entwicklung (+ Backend, Frontend via Vererbung)");
            Console.WriteLine($"      → Default-Berechtigung auf projekte, test: Read|Write (von Entwicklung)");
            Console.WriteLine($"    Anna:   anna.weber / {annaPassword}");
            Console.WriteLine($"      → Mitglied in Backend (Kind von Entwicklung)");
            Console.WriteLine($"      → Default-Berechtigung auf backend-docs: Read|Write (geerbt von Entwicklung)");
            Console.WriteLine($"    Lisa:   lisa.mueller / {lisaPassword}");
            Console.WriteLine($"      → DepartmentAdmin für Marketing");
            Console.WriteLine($"      → Default-Berechtigung auf marketing-files: Read");
            Console.WriteLine($"    Guest:  guest / (leer)");
            Console.WriteLine();
            Console.WriteLine("[+] Hierarchie-Demo:");
            Console.WriteLine($"    Marco (scoped zu Entwicklung) kann Anna bearbeiten,");
            Console.WriteLine($"    weil Anna in Backend ist und Backend Kind von Entwicklung.");
        }

        /// <summary>
        /// Entfernt doppelte Rollen/Gruppen (gleicher Name) und migriert
        /// alle Zuweisungen auf den jeweils ältesten Eintrag.
        /// </summary>
        private async Task RemoveDuplicatesAsync()
        {
            var cleaned = false;

            // ── Doppelte Rollen ──
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

            // ── Doppelte Gruppen ──
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