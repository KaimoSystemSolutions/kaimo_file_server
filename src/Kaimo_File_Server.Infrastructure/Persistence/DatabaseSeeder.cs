using Elastic.Clients.Elasticsearch;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Logging;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

namespace Kaimo_File_Server.Infrastructure.Persistence;

/// <summary>
/// Seeds the database with system roles, default groups, departments,
/// and (on first run) test users with realistic scoped assignments.
///
/// Safe to call on every startup — all operations are idempotent.
///
/// Execution order:
///   1. Clean up duplicates (migration safety net)
///   2. Seed Global department (must exist before Groups/Shares)
///   3. Seed system roles with ManagementPermission flags
///   4. Seed default groups (with DepartmentId = Global)
///   5. Seed department hierarchy with default file permissions
///   6. Seed test users + assignments (first run only)
/// </summary>
public class DatabaseSeeder
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly IPasswordService _passwordService;
    private readonly INtHashProtector _ntHashProtector;
    private readonly ILogger<DatabaseSeeder> _logger;
    private readonly IConfiguration _configuration;

    public DatabaseSeeder(
        IDbContextFactory<ApplicationDbContext> db,
        IPasswordService passwordService,
        INtHashProtector ntHashProtector,
        ILogger<DatabaseSeeder> logger,
        IConfiguration configuration)
    {
        _dbFactory = db;
        _passwordService = passwordService;
        _ntHashProtector = ntHashProtector;
        _logger = logger;
        _configuration = configuration;
    }

    /// <summary>
    /// Whether to seed the demo/test users (admin1234, marco/1234, …). These
    /// are convenient for local development but are a security liability in
    /// production, so they are OFF unless explicitly enabled via
    /// <c>Seed:DemoData=true</c> (set in appsettings.Development.json).
    /// </summary>
    private bool SeedDemoData => _configuration.GetValue("Seed:DemoData", false);

    // ══════════════════════════════════════════
    //  Main Entry Point
    // ══════════════════════════════════════════

    public async Task SeedAsync()
    {
        await CleanupDuplicatesAsync();
        await SeedGlobalDepartmentAsync();
        await SeedSystemGroupsAsync();
        await SeedSystemRolesAsync();

        if (SeedDemoData)
        {
            await SeedDepartmentsAsync();
            await SeedGroupsAsync();
            await SeedTestUsersAsync();
        }
        else
        {
            await SeedBootstrapAdminAsync();
        }

        await SeedConfigAsync();
    }

    // ══════════════════════════════════════════
    //  Bootstrap admin (production / non-demo)
    // ══════════════════════════════════════════

    /// <summary>
    /// Ensures a single administrator account exists so a fresh production
    /// system is reachable — WITHOUT shipping a publicly known password.
    ///
    /// The password is taken from <c>Seed:AdminPassword</c> (env/secret) if
    /// present; otherwise a strong random one is generated and logged exactly
    /// once at startup. There is intentionally no hard-coded fallback.
    /// </summary>
    private async Task SeedBootstrapAdminAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        if (await db.Users.AnyAsync())
        {
            _logger.LogDebug(LogEvents.SeedBootstrapAdminSkipped, LogMessages.SeedBootstrapAdminSkipped);
            return;
        }

        var roles = await LoadRoleLookupAsync();
        if (!roles.TryGetValue("Administrator", out var adminRole))
        {
            _logger.LogWarning(LogEvents.SeedAdminRoleMissing, LogMessages.SeedAdminRoleMissing);
            return;
        }

        string configuredPassword = _configuration["Seed:AdminPassword"];

        var generated = string.IsNullOrWhiteSpace(configuredPassword);
        var password = generated ? GenerateStrongPassword() : configuredPassword!;

        var admin = MakeUser(
            "Administrator", "admin", password,
            "Built-in administrator account", "admin@kaimo.local");

        db.Users.Add(admin);
        await db.SaveChangesAsync();

        db.ScopedRoleAssignments.Add(
            ScopedRoleAssignment.Global(admin.Id, adminRole.Id));
        await db.SaveChangesAsync();

        if (generated)
        {
            _logger.LogWarning(LogEvents.SeedBootstrapAdminPassword, LogMessages.SeedBootstrapAdminPassword, password);
        }
        else
        {
            _logger.LogInformation(LogEvents.SeedBootstrapAdminCreated, LogMessages.SeedBootstrapAdminCreated);
        }
    }

    /// <summary>
    /// Cryptographically strong, URL-safe random password (~24 chars).
    /// </summary>
    private static string GenerateStrongPassword()
    {
        Span<byte> bytes = stackalloc byte[18];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', 'A').Replace('/', 'B').Replace('=', 'C');
    }

    // ══════════════════════════════════════════
    //  0. Global Department (must exist first)
    // ══════════════════════════════════════════

    /// <summary>
    /// Seeds the well-known Global department with the fixed ID
    /// from <see cref="WellKnownGUIDs.DEPARTMENT_GLOBAL"/>.
    ///
    /// This MUST run before Groups and Shares are created,
    /// because their DepartmentId defaults to GlobalId.
    /// </summary>
    private async Task SeedGlobalDepartmentAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var exists = await db.Departments
            .AnyAsync(d => d.Id == WellKnownGUIDs.DEPARTMENT_GLOBAL);

        if (exists)
            return;

        var global = new Department("Global", "Globale Abteilung — Standard für alle Entitäten ohne explizite Zuordnung")
        {
            Id = WellKnownGUIDs.DEPARTMENT_GLOBAL
        };

        db.Departments.Add(global);
        await db.SaveChangesAsync();

        _logger.LogDebug(LogEvents.SeedGlobalDepartmentCreated, LogMessages.SeedGlobalDepartmentCreated,
            WellKnownGUIDs.DEPARTMENT_GLOBAL);
    }

    // ══════════════════════════════════════════
    //  1. System Roles
    // ══════════════════════════════════════════

    /// <summary>
    /// Role definitions with their fixed ManagementPermission flags.
    /// System roles (IsSystemRole=true) cannot be deleted by users.
    /// Their permissions are authoritative — updated on every startup.
    /// </summary>
    private static readonly (string Name, ManagementPermission Perms, bool IsSystem, Guid uuid)[] RoleDefinitions =
    [
        ("Administrator",     ManagementPermission.FullAdmin,                                      true, WellKnownGUIDs.ROLE_ADMIN),
        ("UserManager",       ManagementPermission.UserAdmin | ManagementPermission.AssignGroups,   true, WellKnownGUIDs.ROLE_USER_MANAGER), 
        ("ShareManager",      ManagementPermission.ShareAdmin,                                     true, WellKnownGUIDs.ROLE_SHARE_MANAGER),
        ("DepartmentAdmin",   ManagementPermission.DepartmentAdmin,                                true, WellKnownGUIDs.ROLE_DEPARTMENT_ADMIN),
        ("CertificateManager",ManagementPermission.ManageCertificates,                            true, WellKnownGUIDs.ROLE_CERTIFICATE_MANAGER),
        ("SyncManager",       ManagementPermission.SyncAdmin,                                      true, WellKnownGUIDs.ROLE_SYNC_MANAGER),
        ("User",              ManagementPermission.None,                                           true, WellKnownGUIDs.ROLE_USER),
    ];

    private async Task SeedSystemRolesAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var existing = await db.Roles.ToListAsync();
        var byName = existing.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        var changed = false;

        foreach (var (name, perms, isSystem, uuid) in RoleDefinitions)
        {
            if (byName.TryGetValue(name, out var role))
            {
                if (role.ManagementPermissions != perms || role.IsSystemRole != isSystem)
                {
                    role.ManagementPermissions = perms;
                    role.IsSystemRole = isSystem;
                    _logger.LogDebug(LogEvents.SeedRoleUpdated, LogMessages.SeedRoleUpdated, name, perms);
                    changed = true;
                }
            }
            else
            {
                db.Roles.Add(new Role(uuid, name, perms, isSystem));
                _logger.LogDebug(LogEvents.SeedRoleCreated, LogMessages.SeedRoleCreated, name, perms);
                changed = true;
            }
        }

        if (changed)
            await db.SaveChangesAsync();
    }

    // ══════════════════════════════════════════
    //  2. Default Groups (DepartmentId = Global)
    // ══════════════════════════════════════════

    private static readonly string[] DemoGroups =
    [
        "Developers", "Backend-Team", "Frontend-Team", "Marketing-Team"
    ];

    private static readonly string[] SystemGroups = [
        "Admins", "Everyone"
    ];

    private async Task SeedGroupsAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        
        var existingNames = (await db.Groups.Select(g => g.Name).ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var changed = false;

        foreach (var name in DemoGroups)
        {
            if (existingNames.Add(name))
            {
                // All default groups start in Global department.
                // Department-specific groups get reassigned in SeedTestUsersAsync.
                db.Groups.Add(new Group(Guid.NewGuid(), name));
                _logger.LogDebug(LogEvents.SeedGroupCreated, LogMessages.SeedGroupCreated, name);
                changed = true;
            }
        }

        if (changed)
            await db.SaveChangesAsync();
    }

    private async Task SeedSystemGroupsAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var existingNames = (await db.Groups.Select(g => g.Name).ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var changed = false;

        foreach (var name in SystemGroups)
        {
            if (existingNames.Add(name))
            {
                db.Groups.Add(new Group(Guid.NewGuid(), name));
                _logger.LogDebug(LogEvents.SeedGroupCreated, LogMessages.SeedGroupCreated, name);
                changed = true;
            }
        }

        if (changed)
            await db.SaveChangesAsync();
    }

    // ══════════════════════════════════════════
    //  3. Department Hierarchy
    // ══════════════════════════════════════════

    /// <summary>
    /// Seeds the department hierarchy:
    ///
    ///   Global (well-known, already seeded)
    ///
    ///   Entwicklung (Default: ReadAll | CreateWriteData)
    ///   ├-- Backend    (null → inherits from Entwicklung)
    ///   └-- Frontend   (null → inherits from Entwicklung)
    ///
    ///   Marketing (Default: ReadAll)
    ///
    ///   Geschäftsleitung (Default: ReadAll | WriteAll)
    ///
    /// DefaultFilePermission stores FilePermission flags directly.
    /// These are evaluated as a "virtual allow" layer in the AclService.
    /// null = inherit from parent, explicit value = override.
    /// </summary>
    private async Task SeedDepartmentsAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        // Global is already seeded — only add the others if they don't exist yet
        if (await db.Departments.CountAsync() > 1)
            return;

        // "Schreiben" in the UI = the full write bit set (CreateWriteData |
        // CreateAppendData | WriteAttributes | WriteExtAttributes). Seeding only
        // CreateWriteData would make the UI show "Schreiben" as unchecked.
        const FilePermission writeGroup =
            FilePermission.CreateWriteData | FilePermission.CreateAppendData |
            FilePermission.WriteAttributes | FilePermission.WriteExtAttributes;

        var entwicklung = new Department("Entwicklung", "Software-Entwicklung")
        { DefaultFilePermission = (long)(FilePermission.ReadAll | writeGroup) };

        var marketing = new Department("Marketing", "Marketing & Kommunikation")
        { DefaultFilePermission = (long)FilePermission.ReadAll };

        var geschaeftsl = new Department("Geschäftsleitung", "Unternehmensführung")
        { DefaultFilePermission = (long)(FilePermission.ReadAll | FilePermission.WriteAll) };

        db.Departments.AddRange(entwicklung, marketing, geschaeftsl);
        await db.SaveChangesAsync();

        // Children (inherit permission from Entwicklung)
        var backend = new Department("Backend", "Backend-Entwicklung", entwicklung.Id);
        var frontend = new Department("Frontend", "Frontend-Entwicklung", entwicklung.Id);

        db.Departments.AddRange(backend, frontend);
        await db.SaveChangesAsync();

        _logger.LogDebug(LogEvents.SeedDepartmentsCreated, LogMessages.SeedDepartmentsCreated);
    }

    // ══════════════════════════════════════════
    //  4. Test Users (first run only)
    // ══════════════════════════════════════════

    private async Task SeedTestUsersAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        if (await db.Users.AnyAsync())
        {
            _logger.LogDebug(LogEvents.SeedTestUsersSkipped, LogMessages.SeedTestUsersSkipped);
            return;
        }

        _logger.LogDebug(LogEvents.SeedTestUsersCreating, LogMessages.SeedTestUsersCreating);

        // Load references
        var roles = await LoadRoleLookupAsync();
        var groups = await LoadGroupLookupAsync();
        var departments = await LoadDepartmentLookupAsync();

        // Create shares (with DepartmentId set directly)
        var defaultPoolPath =
            _configuration["Storage:Pools:0:Path"] ?? "/data/storage/pool01";
        var shares = CreateShares(departments, defaultPoolPath);
        db.ShareDefinitions.AddRange(shares.Values);

        // Create users
        var users = CreateUsers();
        db.Users.AddRange(users.Values);

        // Wire everything together
        AssignUsersToGroups(db, users, groups);
        AssignUsersToRoles(db, users, roles);
        AssignShareAccess(db, users, groups, shares);
        AssignUsersToDepartments(db, users, departments);
        AssignGroupsToDepartments(db, groups, departments);

        await db.SaveChangesAsync();

        // Scoped role assignments (need saved IDs)
        CreateScopedAssignments(db, users, roles, departments, groups, shares);
        await db.SaveChangesAsync();

        LogTestUserSummary(users);
    }

    // -- Lookups --

    private async Task<Dictionary<string, Role>> LoadRoleLookupAsync()
        => (await (await _dbFactory.CreateDbContextAsync()).Roles.ToListAsync()).ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);

    private async Task<Dictionary<string, Group>> LoadGroupLookupAsync()
        => (await (await _dbFactory.CreateDbContextAsync()).Groups.ToListAsync()).ToDictionary(g => g.Name, StringComparer.OrdinalIgnoreCase);

    private async Task<Dictionary<string, Department>> LoadDepartmentLookupAsync()
        => (await (await _dbFactory.CreateDbContextAsync()).Departments.ToListAsync()).ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);

    // -- Shares (with direct DepartmentId) --

    /// <summary>
    /// Creates shares with their department assignment set directly via DepartmentId.
    /// No more DepartmentShare join table needed.
    /// </summary>
    private static Dictionary<string, ShareDefinition> CreateShares(
        Dictionary<string, Department> departments,
        string poolPath) => new()
        {
            ["test"] = new ShareDefinition("test", Path.Combine(poolPath, "test"), departments["Entwicklung"].Id),
            ["projekte"] = new ShareDefinition("projekte", Path.Combine(poolPath, "projekte"), departments["Entwicklung"].Id),
            ["general"] = new ShareDefinition("general", Path.Combine(poolPath, "general"), departments["Geschäftsleitung"].Id),
            ["backend-docs"] = new ShareDefinition("backend-docs", Path.Combine(poolPath, "backend-docs"), departments["Backend"].Id),
            ["frontend-docs"] = new ShareDefinition("frontend-docs", Path.Combine(poolPath, "frontend-docs"), departments["Frontend"].Id),
            ["marketing-files"] = new ShareDefinition("marketing-files", Path.Combine(poolPath, "marketing-files"), departments["Marketing"].Id),
        };

    // -- Users --

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

        // No guest/anonymous account: only real, password-protected users exist.
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
            _ntHashProtector.Protect(_passwordService.ComputeNtHash(password)),
            description: description, email: email,
            isEnabled: isEnabled, canChangePassword: canChangePassword);
    }

    // -- Group Assignments --

    private void AssignUsersToGroups(
        ApplicationDbContext db,
        Dictionary<string, User> users,
        Dictionary<string, Group> groups)
    {
        
        var assignments = new (string User, string[] Groups)[]
        {
            ("admin", ["Admins", "Everyone"]),
            ("marco", ["Developers", "Everyone"]),
            ("anna",  ["Backend-Team", "Developers", "Everyone"]),
            ("lisa",  ["Marketing-Team", "Everyone"]),
        };

        foreach (var (userKey, groupNames) in assignments)
        {
            foreach (var groupName in groupNames)
            {
                db.UserGroups.Add(new UserGroup(users[userKey].Id, groups[groupName].Id));
            }
        }
    }

    // -- Role Assignments (global-scoped ScopedRoleAssignments) --

    private void AssignUsersToRoles(
        ApplicationDbContext db,
        Dictionary<string, User> users,
        Dictionary<string, Role> roles)
    {
        var assignments = new (string User, string[] Roles)[]
        {
            ("admin", ["Administrator", "ShareManager", "UserManager"]),
            ("marco", ["User"]),
            ("anna",  ["User"]),
            ("lisa",  ["User"]),
        };

        foreach (var (userKey, roleNames) in assignments)
        {
            foreach (var roleName in roleNames)
            {
                db.ScopedRoleAssignments.Add(
                    ScopedRoleAssignment.Global(users[userKey].Id, roles[roleName].Id));
            }
        }
    }

    // -- Share Access (visibility) --

    private void AssignShareAccess(
        ApplicationDbContext db,
        Dictionary<string, User> users,
        Dictionary<string, Group> groups,
        Dictionary<string, ShareDefinition> shares)
    {
        var userAccess = new (string Share, string User)[]
        {
            ("test", "admin"),
            ("projekte", "admin"),
            ("projekte", "marco"),
        };

        var groupAccess = new (string Share, string Group)[]
        {
            ("test", "Developers"),
            ("general", "Everyone"),
            ("backend-docs", "Backend-Team"),
            ("frontend-docs", "Frontend-Team"),
            ("marketing-files", "Marketing-Team"),
        };

    }

    // -- Department → User (M:N stays) --

    private void AssignUsersToDepartments(
        ApplicationDbContext db,
        Dictionary<string, User> users,
        Dictionary<string, Department> departments)
    {
        var assignments = new (string User, string Department)[]
        {
            ("marco", "Entwicklung"),
            ("anna",  "Backend"),
            ("lisa",  "Marketing"),
        };

        foreach (var (user, dept) in assignments)
            db.DepartmentUsers.Add(new DepartmentUser(departments[dept].Id, users[user].Id));
    }

    // -- Group → Department (direct FK on Group) --

    /// <summary>
    /// Sets the DepartmentId directly on each group entity.
    /// Groups were created with DepartmentId = Global (default).
    /// This method reassigns department-specific groups.
    /// </summary>
    private void AssignGroupsToDepartments(
        ApplicationDbContext db,
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
        {
            groups[group].DepartmentId = departments[dept].Id;
        }

        // Groups not listed here keep their default: Global
        // (Admins, Everyone)
    }

    // -- Scoped Role Assignments --

    /// <summary>
    /// Creates scoped role assignments for delegated administration:
    ///
    ///   admin → Administrator → Global (can manage everything)
    ///   marco → DepartmentAdmin → Entwicklung (can manage Backend + Frontend too)
    ///   lisa  → DepartmentAdmin → Marketing (can only manage Marketing)
    ///   Backend-Team → ShareManager → backend-docs Share
    /// </summary>
    private void CreateScopedAssignments(
        ApplicationDbContext db,
        Dictionary<string, User> users,
        Dictionary<string, Role> roles,
        Dictionary<string, Department> departments,
        Dictionary<string, Group> groups,
        Dictionary<string, ShareDefinition> shares)
    {
        // admin → Administrator (Global) is already assigned in AssignUsersToRoles.

        db.ScopedRoleAssignments.Add(
            ScopedRoleAssignment.ForDepartment(
                users["marco"].Id, roles["DepartmentAdmin"].Id, departments["Entwicklung"].Id));

        db.ScopedRoleAssignments.Add(
            ScopedRoleAssignment.ForDepartment(
                users["lisa"].Id, roles["DepartmentAdmin"].Id, departments["Marketing"].Id));

        db.ScopedRoleAssignments.Add(
            ScopedRoleAssignment.ForShare(
                groups["Backend-Team"].Id, roles["ShareManager"].Id, shares["backend-docs"].Id));
    }

    // -- Config --
    private async Task SeedConfigAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        if (await db.ConfigSettings.AnyAsync())
            return;

        db.ConfigSettings.AddRange(
            new ConfigSetting { Key = "app.language", Value = "de" },
            new ConfigSetting { Key = "app.user.defaultRole", Value = "User" },
            new ConfigSetting { Key = "app.user.isActiveOnCreation", Value = "true" },
            new ConfigSetting { Key = "app.user.maxLoginAttempts", Value = "5" },
            new ConfigSetting { Key = "app.user.lockoutMinutes", Value = "15" },
            new ConfigSetting { Key = "app.user.passwordMinLength", Value = "8" },
            new ConfigSetting { Key = "app.user.requireEmailVerification", Value = "true" },
            new ConfigSetting { Key = "app.group.maxMembers", Value = "50" },
            new ConfigSetting { Key = "app.group.defaultVisibility", Value = "Private" },
            new ConfigSetting { Key = "app.group.allowSelfJoin", Value = "false" }
        );

        await db.SaveChangesAsync();
        _logger.LogDebug(LogEvents.SeedConfigEntries, LogMessages.SeedConfigEntries);
    }

    // -- Logging --

    private void LogTestUserSummary(Dictionary<string, User> users)
    {
        _logger.LogDebug(LogEvents.SeedTestUserSummary, LogMessages.SeedTestUserSummary);
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
        await using var db = await _dbFactory.CreateDbContextAsync();

        var cleaned = false;
        cleaned |= await DeduplicateRolesAsync();
        cleaned |= await DeduplicateGroupsAsync();

        if (cleaned)
            await db.SaveChangesAsync();
    }

    private async Task<bool> DeduplicateRolesAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var allRoles = await db.Roles.ToListAsync();
        var dupes = allRoles
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);

        var cleaned = false;

        foreach (var group in dupes)
        {
            var keep = group.OrderBy(r => r.Id).First();
            var remove = group.Where(r => r.Id != keep.Id).ToList();
            var removeIds = remove.Select(r => r.Id).ToHashSet();

            await MigrateScopedAssignmentsAsync(removeIds, keep.Id);

            db.Roles.RemoveRange(remove);
            _logger.LogInformation(LogEvents.SeedDuplicateRolesRemoved, LogMessages.SeedDuplicateRolesRemoved,
                remove.Count, group.Key);
            cleaned = true;
        }

        return cleaned;
    }

    private async Task<bool> DeduplicateGroupsAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var allGroups = await db.Groups.ToListAsync();
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

            // No more DepartmentGroup migration needed —
            // duplicate groups just get removed, the kept one
            // retains its DepartmentId.

            db.Groups.RemoveRange(remove);
            _logger.LogInformation(LogEvents.SeedDuplicateGroupsRemoved, LogMessages.SeedDuplicateGroupsRemoved,
                remove.Count, group.Key);
            cleaned = true;
        }

        return cleaned;
    }

    // -- Migration Helpers --

    private async Task MigrateScopedAssignmentsAsync(HashSet<Guid> fromRoleIds, Guid toRoleId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var affected = await db.ScopedRoleAssignments
            .Where(a => fromRoleIds.Contains(a.RoleId))
            .ToListAsync();

        foreach (var a in affected)
        {
            var exists = await db.ScopedRoleAssignments.AnyAsync(x =>
                x.PrincipalId == a.PrincipalId &&
                x.RoleId == toRoleId &&
                x.ScopeType == a.ScopeType &&
                x.ScopeId == a.ScopeId);

            if (!exists)
                db.ScopedRoleAssignments.Add(
                    new ScopedRoleAssignment(a.PrincipalId, toRoleId, a.ScopeType, a.ScopeId));

            db.ScopedRoleAssignments.Remove(a);
        }
    }

    private async Task MigrateUserGroupsAsync(HashSet<Guid> fromGroupIds, Guid toGroupId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var affected = await db.UserGroups
            .Where(ug => fromGroupIds.Contains(ug.GroupId))
            .ToListAsync();

        foreach (var ug in affected)
        {
            if (!await db.UserGroups.AnyAsync(x => x.UserId == ug.UserId && x.GroupId == toGroupId))
                db.UserGroups.Add(new UserGroup(ug.UserId, toGroupId));
            db.UserGroups.Remove(ug);
        }
    }
}
