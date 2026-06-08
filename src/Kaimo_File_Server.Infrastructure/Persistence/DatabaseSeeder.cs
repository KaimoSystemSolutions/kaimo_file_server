using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Persistence;

/// <summary>
/// Seeds the database with system roles, default groups, departments,
/// and (on first run) test users with realistic scoped assignments.
///
/// Safe to call on every startup — all operations are idempotent.
///
/// Execution order:
///   1. Clean up duplicates (migration safety net)
///   2. Seed system roles with ManagementPermission flags
///   3. Seed default groups
///   4. Seed department hierarchy with default file permissions
///   5. Seed test users + assignments (first run only)
/// </summary>
public class DatabaseSeeder
{
    private readonly ApplicationDbContext _db;
    private readonly IPasswordService _passwordService;
    private readonly ILogger<DatabaseSeeder> _logger;

    public DatabaseSeeder(
        ApplicationDbContext db,
        IPasswordService passwordService,
        ILogger<DatabaseSeeder> logger)
    {
        _db = db;
        _passwordService = passwordService;
        _logger = logger;
    }

    // ══════════════════════════════════════════
    //  Main Entry Point
    // ══════════════════════════════════════════

    public async Task SeedAsync()
    {
        await CleanupDuplicatesAsync();
        await SeedRolesAsync();
        await SeedGroupsAsync();
        await SeedDepartmentsAsync();
        await SeedTestUsersAsync();
    }

    // ══════════════════════════════════════════
    //  1. System Roles
    // ══════════════════════════════════════════

    /// <summary>
    /// Role definitions with their fixed ManagementPermission flags.
    /// System roles (IsSystemRole=true) cannot be deleted by users.
    /// Their permissions are authoritative — updated on every startup.
    /// </summary>
    private static readonly (string Name, ManagementPermission Perms, bool IsSystem)[] RoleDefinitions =
    [
        ("Administrator",   ManagementPermission.FullAdmin,                                      true),
        ("UserManager",     ManagementPermission.UserAdmin | ManagementPermission.AssignGroups,   true),
        ("ShareManager",    ManagementPermission.ShareAdmin,                                     true),
        ("DepartmentAdmin", ManagementPermission.DepartmentAdmin,                                true),
        ("User",            ManagementPermission.None,                                           true),
    ];

    private async Task SeedRolesAsync()
    {
        var existing = await _db.Roles.ToListAsync();
        var byName = existing.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        var changed = false;

        foreach (var (name, perms, isSystem) in RoleDefinitions)
        {
            if (byName.TryGetValue(name, out var role))
            {
                // Ensure permissions and system flag are current
                if (role.ManagementPermissions != perms || role.IsSystemRole != isSystem)
                {
                    role.ManagementPermissions = perms;
                    role.IsSystemRole = isSystem;
                    _logger.LogInformation("Rolle '{Name}' aktualisiert → {Perms}", name, perms);
                    changed = true;
                }
            }
            else
            {
                _db.Roles.Add(new Role(Guid.NewGuid(), name, perms, isSystem));
                _logger.LogInformation("Rolle '{Name}' angelegt (Permissions: {Perms})", name, perms);
                changed = true;
            }
        }

        if (changed)
            await _db.SaveChangesAsync();
    }

    // ══════════════════════════════════════════
    //  2. Default Groups
    // ══════════════════════════════════════════

    private static readonly string[] DefaultGroups =
    [
        "Admins", "Developers", "Guests", "Everyone",
        "Backend-Team", "Frontend-Team", "Marketing-Team"
    ];

    private async Task SeedGroupsAsync()
    {
        var existingNames = (await _db.Groups.Select(g => g.Name).ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var changed = false;

        foreach (var name in DefaultGroups)
        {
            if (existingNames.Add(name))
            {
                _db.Groups.Add(new Group(Guid.NewGuid(), name));
                _logger.LogInformation("Gruppe '{Name}' angelegt", name);
                changed = true;
            }
        }

        if (changed)
            await _db.SaveChangesAsync();
    }

    // ══════════════════════════════════════════
    //  3. Department Hierarchy
    // ══════════════════════════════════════════

    // FilePermission flags (must match your FilePermission enum)
    private const long PermRead = 1L;
    private const long PermWrite = 2L;
    private const long PermDelete = 4L;

    /// <summary>
    /// Seeds the department hierarchy:
    ///
    ///   Entwicklung (Default: Read|Write)
    ///   ├── Backend    (null → erbt Read|Write)
    ///   └── Frontend   (null → erbt Read|Write)
    ///
    ///   Marketing (Default: Read)
    ///
    ///   Geschäftsleitung (Default: Read|Write|Delete)
    ///
    /// Permission inheritance follows OOP semantics:
    ///   null = inherit from parent, explicit value = override.
    /// </summary>
    private async Task SeedDepartmentsAsync()
    {
        if (await _db.Departments.AnyAsync())
            return;

        // Top-level
        var entwicklung = new Department("Entwicklung", "Software-Entwicklung")
        { DefaultFilePermission = PermRead | PermWrite };

        var marketing = new Department("Marketing", "Marketing & Kommunikation")
        { DefaultFilePermission = PermRead };

        var geschaeftsl = new Department("Geschäftsleitung", "Unternehmensführung")
        { DefaultFilePermission = PermRead | PermWrite | PermDelete };

        _db.Departments.AddRange(entwicklung, marketing, geschaeftsl);
        await _db.SaveChangesAsync();

        // Children (inherit permission from Entwicklung)
        var backend = new Department("Backend", "Backend-Entwicklung", entwicklung.Id);
        var frontend = new Department("Frontend", "Frontend-Entwicklung", entwicklung.Id);

        _db.Departments.AddRange(backend, frontend);
        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Departments angelegt: Entwicklung (→ Backend, Frontend), Marketing, Geschäftsleitung");
    }

    // ══════════════════════════════════════════
    //  4. Test Users (first run only)
    // ══════════════════════════════════════════

    private async Task SeedTestUsersAsync()
    {
        if (await _db.Users.AnyAsync())
        {
            _logger.LogDebug("Users bereits vorhanden — überspringe Test-User");
            return;
        }

        _logger.LogInformation("Erstelle Testbenutzer...");

        // Load references
        var roles = await LoadRoleLookupAsync();
        var groups = await LoadGroupLookupAsync();
        var departments = await LoadDepartmentLookupAsync();

        // Create shares
        var shares = CreateShares();
        _db.ShareDefinitions.AddRange(shares.Values);

        // Create users
        var users = CreateUsers();
        _db.Users.AddRange(users.Values);

        // Wire everything together
        AssignUsersToGroups(users, groups);
        AssignUsersToRoles(users, roles);
        AssignShareAccess(users, groups, shares);
        AssignUsersToDepartments(users, departments);
        AssignGroupsToDepartments(groups, departments);
        AssignSharesToDepartments(shares, departments);

        await _db.SaveChangesAsync();

        // Scoped role assignments (need saved IDs)
        CreateScopedAssignments(users, roles, departments, groups, shares);
        await _db.SaveChangesAsync();

        LogTestUserSummary(users);
    }

    // ── Lookups ──

    private async Task<Dictionary<string, Role>> LoadRoleLookupAsync()
        => (await _db.Roles.ToListAsync()).ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);

    private async Task<Dictionary<string, Group>> LoadGroupLookupAsync()
        => (await _db.Groups.ToListAsync()).ToDictionary(g => g.Name, StringComparer.OrdinalIgnoreCase);

    private async Task<Dictionary<string, Department>> LoadDepartmentLookupAsync()
        => (await _db.Departments.ToListAsync()).ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);

    // ── Shares ──

    private static Dictionary<string, ShareDefinition> CreateShares() => new()
    {
        ["test"] = new ShareDefinition("test", "/data/storage/test"),
        ["projekte"] = new ShareDefinition("projekte", "/data/storage/projekte"),
        ["general"] = new ShareDefinition("general", "/data/storage/general"),
        ["backend-docs"] = new ShareDefinition("backend-docs", "/data/storage/backend-docs"),
        ["frontend-docs"] = new ShareDefinition("frontend-docs", "/data/storage/frontend-docs"),
        ["marketing-files"] = new ShareDefinition("marketing-files", "/data/storage/marketing-files"),
    };

    // ── Users ──

    private Dictionary<string, User> CreateUsers()
    {
        var users = new Dictionary<string, User>(StringComparer.OrdinalIgnoreCase);

        users["admin"] = MakeUser(
            "Administrator", "admin", "admin1234",
            "Built-in administrator account", "admin@kaimo.local");

        users["marco"] = MakeUser(
            "Marco Hanisch", "marco.hanisch", "1234",
            "Abteilungsleiter Entwicklung", "marco.hanisch@kaimo.local");

        users["anna"] = MakeUser(
            "Anna Weber", "anna.weber", "1234",
            "Backend-Entwicklerin", "anna.weber@kaimo.local");

        users["lisa"] = MakeUser(
            "Lisa Müller", "lisa.mueller", "1234",
            "Marketing-Leiterin", "lisa.mueller@kaimo.local");

        users["guest"] = MakeUser(
            "Guest", "guest", "",
            "Built-in guest account", "",
            canChangePassword: false);

        return users;
    }

    private User MakeUser(
        string name, string username, string password,
        string description, string email,
        bool isEnabled = true, bool canChangePassword = true)
    {
        return new User(
            Guid.NewGuid(), name, username,
            _passwordService.HashPassword(password),
            _passwordService.ComputeNtHash(password),
            description: description, email: email,
            isEnabled: isEnabled, canChangePassword: canChangePassword);
    }

    // ── Group Assignments ──

    private void AssignUsersToGroups(
        Dictionary<string, User> users,
        Dictionary<string, Group> groups)
    {
        var assignments = new (string User, string[] Groups)[]
        {
            ("admin", ["Admins", "Everyone"]),
            ("marco", ["Developers", "Everyone"]),
            ("anna",  ["Backend-Team", "Developers", "Everyone"]),
            ("lisa",  ["Marketing-Team", "Everyone"]),
            ("guest", ["Guests", "Everyone"]),
        };

        foreach (var (userKey, groupNames) in assignments)
        {
            foreach (var groupName in groupNames)
            {
                _db.UserGroups.Add(new UserGroup(users[userKey].Id, groups[groupName].Id));
            }
        }
    }

    // ── Role Assignments (direct → Global scope in ManagementAuthService) ──

    private void AssignUsersToRoles(
        Dictionary<string, User> users,
        Dictionary<string, Role> roles)
    {
        var assignments = new (string User, string[] Roles)[]
        {
            ("admin", ["Administrator", "ShareManager", "UserManager"]),
            ("marco", ["User"]),
            ("anna",  ["User"]),
            ("lisa",  ["User"]),
            ("guest", ["User"]),
        };

        foreach (var (userKey, roleNames) in assignments)
        {
            foreach (var roleName in roleNames)
            {
                _db.UserRoles.Add(new UserRole(users[userKey].Id, roles[roleName].Id));
            }
        }
    }

    // ── Share Access (visibility) ──

    private void AssignShareAccess(
        Dictionary<string, User> users,
        Dictionary<string, Group> groups,
        Dictionary<string, ShareDefinition> shares)
    {
        // User-level access
        var userAccess = new (string Share, string User)[]
        {
            ("test", "admin"),
            ("projekte", "admin"),
            ("projekte", "marco"),
        };

        foreach (var (share, user) in userAccess)
            _db.ShareAccessEntries.Add(new ShareAccessEntry(share, users[user].Id));

        // Group-level access
        var groupAccess = new (string Share, string Group)[]
        {
            ("test", "Developers"),
            ("general", "Everyone"),
            ("backend-docs", "Backend-Team"),
            ("frontend-docs", "Frontend-Team"),
            ("marketing-files", "Marketing-Team"),
        };

        foreach (var (share, group) in groupAccess)
            _db.ShareAccessEntries.Add(new ShareAccessEntry(share, groups[group].Id));
    }

    // ── Department → User ──

    private void AssignUsersToDepartments(
        Dictionary<string, User> users,
        Dictionary<string, Department> departments)
    {
        var assignments = new (string User, string Department)[]
        {
            ("marco", "Entwicklung"),  // Top-level: Abteilungsleiter
            ("anna",  "Backend"),      // Child of Entwicklung
            ("lisa",  "Marketing"),
        };

        foreach (var (user, dept) in assignments)
            _db.DepartmentUsers.Add(new DepartmentUser(departments[dept].Id, users[user].Id));
    }

    // ── Department → Group ──

    private void AssignGroupsToDepartments(
        Dictionary<string, Group> groups,
        Dictionary<string, Department> departments)
    {
        var assignments = new (string Group, string Department)[]
        {
            ("Developers",    "Entwicklung"),
            ("Backend-Team",  "Backend"),
            ("Frontend-Team", "Frontend"),
            ("Marketing-Team","Marketing"),
        };

        foreach (var (group, dept) in assignments)
            _db.DepartmentGroups.Add(new DepartmentGroup(departments[dept].Id, groups[group].Id));
    }

    // ── Department → Share ──

    private void AssignSharesToDepartments(
        Dictionary<string, ShareDefinition> shares,
        Dictionary<string, Department> departments)
    {
        var assignments = new (string Share, string Department)[]
        {
            ("projekte",        "Entwicklung"),      // Gilt auch für Backend/Frontend via Hierarchie
            ("test",            "Entwicklung"),
            ("backend-docs",    "Backend"),
            ("frontend-docs",   "Frontend"),
            ("marketing-files", "Marketing"),
            ("general",         "Geschäftsleitung"),
        };

        foreach (var (share, dept) in assignments)
            _db.DepartmentShares.Add(new DepartmentShare(departments[dept].Id, shares[share].Id));
    }

    // ── Scoped Role Assignments ──

    /// <summary>
    /// Creates scoped role assignments for delegated administration:
    ///
    ///   admin → Administrator → Global (can manage everything)
    ///   marco → DepartmentAdmin → Entwicklung (can manage Backend + Frontend too)
    ///   lisa  → DepartmentAdmin → Marketing (can only manage Marketing)
    ///   Backend-Team → ShareManager → backend-docs Share
    /// </summary>
    private void CreateScopedAssignments(
        Dictionary<string, User> users,
        Dictionary<string, Role> roles,
        Dictionary<string, Department> departments,
        Dictionary<string, Group> groups,
        Dictionary<string, ShareDefinition> shares)
    {
        // Global admin
        _db.ScopedRoleAssignments.Add(
            ScopedRoleAssignment.Global(users["admin"].Id, roles["Administrator"].Id));

        // Department-scoped admins
        _db.ScopedRoleAssignments.Add(
            ScopedRoleAssignment.ForDepartment(
                users["marco"].Id, roles["DepartmentAdmin"].Id, departments["Entwicklung"].Id));

        _db.ScopedRoleAssignments.Add(
            ScopedRoleAssignment.ForDepartment(
                users["lisa"].Id, roles["DepartmentAdmin"].Id, departments["Marketing"].Id));

        // Share-scoped group permission
        _db.ScopedRoleAssignments.Add(
            ScopedRoleAssignment.ForShare(
                groups["Backend-Team"].Id, roles["ShareManager"].Id, shares["backend-docs"].Id));
    }

    // ── Logging ──

    private void LogTestUserSummary(Dictionary<string, User> users)
    {
        _logger.LogInformation("""
            Testbenutzer erstellt:
              admin / admin1234         → Global Admin
              marco.hanisch / 1234      → DepartmentAdmin Entwicklung (+ Backend, Frontend)
              anna.weber / 1234         → Backend-Mitglied, Default: Read|Write (geerbt)
              lisa.mueller / 1234       → DepartmentAdmin Marketing, Default: Read
              guest / (leer)            → Gast-Konto
            """);
    }

    // ══════════════════════════════════════════
    //  Duplicate Cleanup
    // ══════════════════════════════════════════

    /// <summary>
    /// Removes duplicate roles and groups (same name), migrating all
    /// assignments to the oldest entry. Safety net for repeated seeding
    /// or manual DB edits.
    /// </summary>
    private async Task CleanupDuplicatesAsync()
    {
        var cleaned = false;
        cleaned |= await DeduplicateRolesAsync();
        cleaned |= await DeduplicateGroupsAsync();

        if (cleaned)
            await _db.SaveChangesAsync();
    }

    private async Task<bool> DeduplicateRolesAsync()
    {
        var allRoles = await _db.Roles.ToListAsync();
        var dupes = allRoles
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);

        var cleaned = false;

        foreach (var group in dupes)
        {
            var keep = group.OrderBy(r => r.Id).First();
            var remove = group.Where(r => r.Id != keep.Id).ToList();
            var removeIds = remove.Select(r => r.Id).ToHashSet();

            // Migrate UserRole references
            await MigrateUserRolesAsync(removeIds, keep.Id);

            // Migrate ScopedRoleAssignment references
            await MigrateScopedAssignmentsAsync(removeIds, keep.Id);

            _db.Roles.RemoveRange(remove);
            _logger.LogInformation(
                "{Count} doppelte Rolle(n) '{Name}' entfernt", remove.Count, group.Key);
            cleaned = true;
        }

        return cleaned;
    }

    private async Task<bool> DeduplicateGroupsAsync()
    {
        var allGroups = await _db.Groups.ToListAsync();
        var dupes = allGroups
            .GroupBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);

        var cleaned = false;

        foreach (var group in dupes)
        {
            var keep = group.OrderBy(g => g.Id).First();
            var remove = group.Where(g => g.Id != keep.Id).ToList();
            var removeIds = remove.Select(g => g.Id).ToHashSet();

            await MigrateUserGroupsAsync(removeIds, keep.Id);
            await MigrateShareAccessAsync(removeIds, keep.Id);
            await MigrateDepartmentGroupsAsync(removeIds, keep.Id);

            _db.Groups.RemoveRange(remove);
            _logger.LogInformation(
                "{Count} doppelte Gruppe(n) '{Name}' entfernt", remove.Count, group.Key);
            cleaned = true;
        }

        return cleaned;
    }

    // ── Migration Helpers ──

    private async Task MigrateUserRolesAsync(HashSet<Guid> fromRoleIds, Guid toRoleId)
    {
        var affected = await _db.UserRoles
            .Where(ur => fromRoleIds.Contains(ur.RoleId))
            .ToListAsync();

        foreach (var ur in affected)
        {
            if (!await _db.UserRoles.AnyAsync(x => x.UserId == ur.UserId && x.RoleId == toRoleId))
                _db.UserRoles.Add(new UserRole(ur.UserId, toRoleId));
            _db.UserRoles.Remove(ur);
        }
    }

    private async Task MigrateScopedAssignmentsAsync(HashSet<Guid> fromRoleIds, Guid toRoleId)
    {
        var affected = await _db.ScopedRoleAssignments
            .Where(a => fromRoleIds.Contains(a.RoleId))
            .ToListAsync();

        foreach (var a in affected)
        {
            var exists = await _db.ScopedRoleAssignments.AnyAsync(x =>
                x.PrincipalId == a.PrincipalId &&
                x.RoleId == toRoleId &&
                x.ScopeType == a.ScopeType &&
                x.ScopeId == a.ScopeId);

            if (!exists)
                _db.ScopedRoleAssignments.Add(
                    new ScopedRoleAssignment(a.PrincipalId, toRoleId, a.ScopeType, a.ScopeId));

            _db.ScopedRoleAssignments.Remove(a);
        }
    }

    private async Task MigrateUserGroupsAsync(HashSet<Guid> fromGroupIds, Guid toGroupId)
    {
        var affected = await _db.UserGroups
            .Where(ug => fromGroupIds.Contains(ug.GroupId))
            .ToListAsync();

        foreach (var ug in affected)
        {
            if (!await _db.UserGroups.AnyAsync(x => x.UserId == ug.UserId && x.GroupId == toGroupId))
                _db.UserGroups.Add(new UserGroup(ug.UserId, toGroupId));
            _db.UserGroups.Remove(ug);
        }
    }

    private async Task MigrateShareAccessAsync(HashSet<Guid> fromGroupIds, Guid toGroupId)
    {
        var affected = await _db.ShareAccessEntries
            .Where(sa => fromGroupIds.Contains(sa.PrincipalId))
            .ToListAsync();

        foreach (var sa in affected)
        {
            if (!await _db.ShareAccessEntries.AnyAsync(
                    x => x.ShareName == sa.ShareName && x.PrincipalId == toGroupId))
                _db.ShareAccessEntries.Add(new ShareAccessEntry(sa.ShareName, toGroupId));
            _db.ShareAccessEntries.Remove(sa);
        }
    }

    private async Task MigrateDepartmentGroupsAsync(HashSet<Guid> fromGroupIds, Guid toGroupId)
    {
        var affected = await _db.DepartmentGroups
            .Where(dg => fromGroupIds.Contains(dg.GroupId))
            .ToListAsync();

        foreach (var dg in affected)
        {
            if (!await _db.DepartmentGroups.AnyAsync(
                    x => x.DepartmentId == dg.DepartmentId && x.GroupId == toGroupId))
                _db.DepartmentGroups.Add(new DepartmentGroup(dg.DepartmentId, toGroupId));
            _db.DepartmentGroups.Remove(dg);
        }
    }
}