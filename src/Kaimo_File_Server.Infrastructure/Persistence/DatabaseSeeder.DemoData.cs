using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Logging;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Persistence;

public partial class DatabaseSeeder
{
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
            // marco/anna/lisa intentionally get no management role — they are ordinary users.
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
}
