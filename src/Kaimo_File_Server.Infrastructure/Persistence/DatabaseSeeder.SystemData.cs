using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Logging;
using Kaimo_File_Server.Core.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Persistence;

public partial class DatabaseSeeder
{
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

    /// <summary>
    /// The Global department stands for the whole system and is a pure placeholder: a role
    /// assigned "at department Global" is a system-wide (<see cref="ScopeType.Global"/>)
    /// assignment, every department without a parent hangs below it, and it carries no file
    /// defaults and no explicit members. Converges installs that predate this meaning;
    /// idempotent, runs on every startup.
    /// </summary>
    private async Task NormalizeGlobalDepartmentAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var globalId = WellKnownGUIDs.DEPARTMENT_GLOBAL;

        var deptScoped = await db.ScopedRoleAssignments
            .Where(a => a.ScopeType == ScopeType.Department && a.ScopeId == globalId)
            .ToListAsync();
        foreach (var a in deptScoped)
        {
            var hasGlobalTwin = await db.ScopedRoleAssignments.AnyAsync(x =>
                x.PrincipalId == a.PrincipalId && x.RoleId == a.RoleId
                && x.ScopeType == ScopeType.Global && x.ScopeId == Guid.Empty);
            if (hasGlobalTwin)
            {
                db.ScopedRoleAssignments.Remove(a);
            }
            else
            {
                a.ScopeType = ScopeType.Global;
                a.ScopeId = Guid.Empty;
            }
        }

        var children = await db.Departments.Where(d => d.ParentDepartmentId == globalId).ToListAsync();
        foreach (var d in children)
            d.ParentDepartmentId = null;

        var global = await db.Departments.FindAsync(globalId);
        var clearedDefault = global?.DefaultFilePermission is not null;
        if (clearedDefault)
            global!.DefaultFilePermission = null;

        var members = await db.DepartmentUsers.Where(du => du.DepartmentId == globalId).ToListAsync();
        db.DepartmentUsers.RemoveRange(members);

        if (deptScoped.Count == 0 && children.Count == 0 && !clearedDefault && members.Count == 0)
            return;

        await db.SaveChangesAsync();
        _logger.LogInformation(
            "Normalized the Global department: {Assignments} assignment(s) made system-wide, " +
            "{Children} department(s) moved to the top level, file default cleared: {Cleared}, " +
            "{Members} explicit membership(s) removed.",
            deptScoped.Count, children.Count, clearedDefault, members.Count);
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
        // ManageHomes/AccessHomeContents stay outside FullAdmin: FullAdmin doubles as the "is global admin" test, so a
        // new bit there would demote custom roles that were built from the FullAdmin preset.
        ("Administrator",     ManagementPermission.FullAdmin | ManagementPermission.ManageHomes | ManagementPermission.AccessHomeContents | ManagementPermission.ManageMailServer | ManagementPermission.ManageNotifications | ManagementPermission.FileBackupAdmin, true, WellKnownGUIDs.ROLE_ADMIN),
        ("UserManager",       ManagementPermission.UserAdmin | ManagementPermission.AssignGroups,   true, WellKnownGUIDs.ROLE_USER_MANAGER), 
        ("ShareManager",      ManagementPermission.ShareAdmin,                                     true, WellKnownGUIDs.ROLE_SHARE_MANAGER),
        ("DepartmentAdmin",   ManagementPermission.DepartmentAdmin,                                true, WellKnownGUIDs.ROLE_DEPARTMENT_ADMIN),
        ("CertificateManager",ManagementPermission.ManageCertificates,                            true, WellKnownGUIDs.ROLE_CERTIFICATE_MANAGER),
        ("SyncManager",       ManagementPermission.SyncAdmin,                                      true, WellKnownGUIDs.ROLE_SYNC_MANAGER),
        ("BackupManager",     ManagementPermission.ManageBackups,                                  true, WellKnownGUIDs.ROLE_BACKUP_MANAGER),
        ("ClientDeviceManager", ManagementPermission.ManageClientDevices,                          true, WellKnownGUIDs.ROLE_CLIENT_DEVICE_MANAGER),
        ("NotificationManager", ManagementPermission.ManageNotifications,                          true, WellKnownGUIDs.ROLE_NOTIFICATION_MANAGER),
        // Meant to be assigned at department or share scope: run and restore backups of those
        // shares on repositories released to the department. Repositories stay Global-only.
        ("BackupOperator",    ManagementPermission.BackupOperator,                                 true, WellKnownGUIDs.ROLE_BACKUP_OPERATOR),
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

    /// <summary>
    /// Retires the obsolete "User" system role. It only ever granted
    /// <see cref="ManagementPermission.None"/>, so dropping it and its
    /// assignments leaves the affected principals as ordinary users with no
    /// change in effective rights. Runs on every startup so already-seeded
    /// installs converge — <see cref="SeedSystemRolesAsync"/> never removes a
    /// definition that was taken out of <c>RoleDefinitions</c>, and a system
    /// role cannot be deleted through the UI.
    /// </summary>
    private async Task RetireUserRoleAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        // Match by name + IsSystemRole so a user's own custom role named "User"
        // (IsSystemRole == false) is never touched. Covers legacy installs where
        // the seeded role may not carry the well-known id.
        var obsolete = await db.Roles
            .Where(r => r.IsSystemRole && r.Name == "User")
            .Select(r => r.Id)
            .ToListAsync();

        if (obsolete.Count == 0)
            return;

        await db.ScopedRoleAssignments
            .Where(a => obsolete.Contains(a.RoleId))
            .ExecuteDeleteAsync();
        await db.Roles
            .Where(r => obsolete.Contains(r.Id))
            .ExecuteDeleteAsync();

        _logger.LogInformation(LogEvents.SeedDuplicateRolesRemoved, LogMessages.SeedDuplicateRolesRemoved,
            obsolete.Count, "User");
    }

    // ══════════════════════════════════════════
    //  2. Default Groups (DepartmentId = Global)
    // ══════════════════════════════════════════

    private static readonly string[] DemoGroups =
    [
        "Developers", "Backend-Team", "Frontend-Team", "Marketing-Team"
    ];

    // System groups have fixed well-known IDs so code can reference them without a
    // fragile name lookup. Order does not matter.
    private static readonly (Guid Id, string Name)[] SystemGroupDefinitions =
    [
        (WellKnownGUIDs.GROUP_ADMINS,   "Admins"),
        (WellKnownGUIDs.GROUP_EVERYONE, "Everyone"),
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

        var allGroups = await db.Groups.ToListAsync();
        var changed = false;

        foreach (var (wellKnownId, name) in SystemGroupDefinitions)
        {
            // Already present under its well-known id: nothing to do.
            if (allGroups.Any(g => g.Id == wellKnownId))
                continue;

            // Legacy install: a group of this name exists with a random id (pre-well-known
            // GUIDs). Remap it — and everything referencing it — onto the well-known id.
            var legacy = allGroups.FirstOrDefault(
                g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
            if (legacy is not null)
            {
                await RemapGroupIdAsync(db, legacy.Id, wellKnownId, name);
                continue;
            }

            db.Groups.Add(new Group(wellKnownId, name));
            _logger.LogDebug(LogEvents.SeedGroupCreated, LogMessages.SeedGroupCreated, name);
            changed = true;
        }

        if (changed)
            await db.SaveChangesAsync();
    }

    /// <summary>
    /// Repoints a group onto a new id. <see cref="Group.Id"/> is the PK and is referenced
    /// by plain <see cref="Guid"/> columns with no FK cascade, and EF cannot mutate a PK in
    /// place — so repoint every reference (mirroring <c>GroupRepository.DeleteAsync</c>),
    /// then swap the row. Provider-agnostic via <c>ExecuteUpdate</c> (no raw SQL).
    /// </summary>
    private async Task RemapGroupIdAsync(
        ApplicationDbContext db, Guid oldId, Guid newId, string name)
    {
        await db.UserGroups.Where(ug => ug.GroupId == oldId)
            .ExecuteUpdateAsync(s => s.SetProperty(ug => ug.GroupId, newId));
        await db.ScopedRoleAssignments.Where(a => a.PrincipalId == oldId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.PrincipalId, newId));
        await db.CloudAccessGrants.Where(g => g.PrincipalId == oldId)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.PrincipalId, newId));

        var old = await db.Groups.FindAsync(oldId);
        if (old is not null) db.Groups.Remove(old);
        db.Groups.Add(new Group(newId, name));
        await db.SaveChangesAsync();

        _logger.LogInformation(
            "Remapped system group '{Name}' from {OldId} to well-known id {NewId}.",
            name, oldId, newId);
    }

    /// <summary>
    /// Ensures every user is a member of the global Everyone group. Idempotent; only
    /// adds the missing rows. Covers both existing installs (upgrade backfill) and any
    /// user that reached the database without going through the normal create path.
    /// </summary>
    private async Task BackfillEveryoneMembershipAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var everyoneId = WellKnownGUIDs.GROUP_EVERYONE;
        var missing = await db.Users
            .Where(u => !db.UserGroups.Any(ug => ug.UserId == u.Id && ug.GroupId == everyoneId))
            .Select(u => u.Id)
            .ToListAsync();

        if (missing.Count == 0)
            return;

        db.UserGroups.AddRange(missing.Select(uid => new UserGroup(uid, everyoneId)));
        await db.SaveChangesAsync();
        _logger.LogInformation("Backfilled {Count} user(s) into the Everyone group.", missing.Count);
    }
}
