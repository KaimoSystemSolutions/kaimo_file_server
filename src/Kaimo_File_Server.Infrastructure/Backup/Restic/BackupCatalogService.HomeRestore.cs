using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;

namespace Kaimo_File_Server.Infrastructure.Backup.Restic;

public partial class BackupCatalogService
{
    // ══════════════════════════════════════════
    //  Home folders in backups (same rule as the live home grant, AclService.HomeAccess)
    // ══════════════════════════════════════════

    /// <summary>
    /// The home-folder share in backups needs <see cref="ManagementPermission.AccessHomeContents"/>
    /// at any scope on top of the restore bits, like listing the live share root.
    /// </summary>
    private Task<bool> CanSeeHomesAsync(UserContext actor)
        => auth.HasAnyPermissionAsync(actor, ManagementPermission.AccessHomeContents);

    /// <summary>
    /// A path below the homes root belongs to the owner named by its first segment
    /// (<c>HomeDirectoryService.HomePathOf</c>, Guid "D" format); the actor must be allowed to
    /// manage that owner for <see cref="ManagementPermission.AccessHomeContents"/> (scope + rank).
    /// Anything else (the root itself, non-home entries) is denied. Decisions are cached in
    /// <paramref name="owners"/> for one call.
    /// </summary>
    private async Task<bool> MayAccessHomePathAsync(UserContext actor, string relativePath, Dictionary<Guid, bool> owners)
    {
        var slash = relativePath.IndexOf('/');
        var home = slash < 0 ? relativePath : relativePath[..slash];
        if (!Guid.TryParseExact(home, "D", out var owner))
            return false;
        if (!owners.TryGetValue(owner, out var allowed))
            owners[owner] = allowed = await auth.CanManageUserAsync(actor, owner, ManagementPermission.AccessHomeContents);
        return allowed;
    }

    private async Task RequireHomePathAsync(UserContext actor, string relativePath)
    {
        if (!await MayAccessHomePathAsync(actor, relativePath, []))
            throw new UnauthorizedAccessException("The home folder is outside the actor's reach.");
    }

    /// <summary>
    /// Every home a restore of <paramref name="items"/> in <paramref name="folder"/> touches must be
    /// reachable. Returns the items to restore: the whole root becomes every home it holds (each of
    /// which must be reachable); entries at the root that are no home are left out, as the live
    /// share never exposes them.
    /// </summary>
    private async Task<List<string>> RequireHomeOwnersAsync(UserContext actor, BackupSnapshot snapshot, BackupRepository repository,
        string folder, List<string> items, CancellationToken ct)
    {
        if (folder.Length > 0)
        {
            await RequireHomePathAsync(actor, folder);
            return items;
        }
        var names = items.Count > 0
            ? items
            : (await ListSnapshotFolderAsync(snapshot, repository, folder, ct))
                .Select(e => e.Name).Where(n => Guid.TryParseExact(n, "D", out _)).ToList();
        if (names.Count == 0)
            throw new ResticException("path_invalid", "The backup point holds no home folder to restore.");
        var owners = new Dictionary<Guid, bool>();
        using var scope = auth.BeginEvaluationScope();
        foreach (var name in names)
        {
            if (!await MayAccessHomePathAsync(actor, name, owners))
                throw new UnauthorizedAccessException("A home folder of the restore is outside the actor's reach.");
        }
        return names;
    }

    /// <summary>The homes root shows only the homes the actor may reach.</summary>
    private async Task<IReadOnlyList<BackupBrowseEntry>> FilterHomesRootAsync(UserContext actor, IReadOnlyList<BackupBrowseEntry> entries)
    {
        var owners = new Dictionary<Guid, bool>();
        var visible = new List<BackupBrowseEntry>();
        using var scope = auth.BeginEvaluationScope();
        foreach (var entry in entries)
        {
            if (await MayAccessHomePathAsync(actor, entry.Name, owners))
                visible.Add(entry);
        }
        return visible;
    }
}
