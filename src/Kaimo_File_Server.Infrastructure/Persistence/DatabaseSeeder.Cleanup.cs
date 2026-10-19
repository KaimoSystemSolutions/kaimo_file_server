using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Logging;
using Kaimo_File_Server.Core.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Persistence;

public partial class DatabaseSeeder
{
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
