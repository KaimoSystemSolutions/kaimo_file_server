using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Logging;
using Kaimo_File_Server.Core.Repositories.Shares;
using Kaimo_File_Server.Core.Security.Management;
using Kaimo_File_Server.Core.Services.Management;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Admin operations that provision a virtual share as a folder inside a local share. The
/// mount point exists only in the database; its name is reserved against user writes by the
/// mount guard. Methods return <c>null</c> on success, otherwise a resource key.
/// </summary>
public sealed class CloudAccessMountService(
    ICloudAccessRepository repository,
    IShareRepository shares,
    CloudAccessAuthorizationService authorization,
    IManagementAuthService managementAuth,
    ILogger<CloudAccessMountService> logger)
{
    /// <summary>Local shares the actor may host a mount in (never the home-folder share).</summary>
    public async Task<List<ShareDefinition>> GetHostSharesAsync(UserContext actor)
    {
        var hosts = new List<ShareDefinition>();
        foreach (var share in await shares.GetAllEnabledAsync())
            if (!share.IsUserHomes &&
                await managementAuth.CanManageShareAsync(actor, share.Id, ManagementPermission.EditShareSettings))
                hosts.Add(share);
        return hosts.OrderBy(share => share.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Sub-folders of <paramref name="path"/> on disk, for the parent-folder picker.</summary>
    public IReadOnlyList<(string Name, string Path)> ListHostFolders(ShareDefinition host, string path)
    {
        var normalized = ShareRelativePath.Normalize(path);
        var absolute = ShareRelativePath.ToContainedAbsolutePath(host.Path, normalized, allowInternalNamespace: false);
        if (!Directory.Exists(absolute)) return [];
        return new DirectoryInfo(absolute)
            .EnumerateDirectories()
            .Where(dir => (dir.Attributes & FileAttributes.ReparsePoint) == 0
                          && !ShareEntryPolicy.IsReservedForUserWrites(dir.Name))
            .OrderBy(dir => dir.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(dir => (dir.Name, ShareRelativePath.Combine(normalized, dir.Name)))
            .ToList();
    }

    /// <summary>Mounts <paramref name="share"/> as folder <paramref name="folderName"/> below <paramref name="parentPath"/>.</summary>
    public async Task<string?> MountAsync(
        UserContext actor, CloudAccessShare share, Guid hostShareId, string? parentPath, string? folderName)
    {
        if (!await authorization.CanManageAsync(actor, share) ||
            !await managementAuth.CanManageShareAsync(actor, hostShareId, ManagementPermission.EditShareSettings))
            return "Web_CloudAccess_Mount_Error_NotAllowed";
        var host = await shares.GetByIdAsync(hostShareId);
        if (host is null || !host.IsEnabled) return "Web_CloudAccess_Mount_Error_NotAllowed";
        if (host.IsUserHomes) return "Web_CloudAccess_Mount_Error_HomesShare";

        var name = folderName?.Trim() ?? "";
        if (name.Length == 0) return "Web_CloudAccess_Mount_Error_RootNotAllowed";
        if (!WindowsFileNameHelper.IsValid(name) ||
            !ShareRelativePath.TryNormalizeStrict(parentPath ?? "", out var parent, allowInternalNamespace: true) ||
            !ShareRelativePath.TryNormalizeStrict(ShareRelativePath.Combine(parent, name), out var path,
                allowRoot: false, allowInternalNamespace: true))
            return "Web_CloudAccess_Mount_Error_InvalidPath";
        if (path.Split('/').Any(segment => ShareEntryPolicy.IsReservedForUserWrites(segment)))
            return "Web_CloudAccess_Mount_Error_ReservedName";

        var others = (await repository.GetSharesAsync())
            .Where(other => other.Id != share.Id && other.MountShareId == host.Id && other.IsMounted);
        if (others.Any(other => ShareRelativePath.IsSameOrDescendant(other.MountPath!, path)
                                || ShareRelativePath.IsSameOrDescendant(path, other.MountPath!)))
            return "Web_CloudAccess_Mount_Error_Nested";

        var parentAbsolute = ShareRelativePath.ToContainedAbsolutePath(host.Path, parent);
        if (!Directory.Exists(parentAbsolute)) return "Web_CloudAccess_Mount_Error_ParentMissing";
        if (NameTaken(parentAbsolute, name)) return "Web_CloudAccess_Mount_Error_NameInUse";

        var previous = (share.Provisioning, share.MountShareId, share.MountPath);
        try
        {
            await repository.SetMountAsync(share.Id, CloudAccessProvisioning.Folder, host.Id, path);
        }
        catch (DbUpdateException)
        {
            return "Web_CloudAccess_Mount_Error_NameInUse";
        }

        // ponytail: a create that passed the reservation check just before the insert can still
        // land between this re-check and its own write; the Web listing then shows the mount.
        if (NameTaken(parentAbsolute, name))
        {
            await repository.SetMountAsync(share.Id, previous.Provisioning, previous.MountShareId, previous.MountPath);
            logger.LogWarning(LogEvents.CloudAccessMountRaceRolledBack, LogMessages.CloudAccessMountRaceRolledBack,
                share.Name, path);
            return "Web_CloudAccess_Mount_Error_NameInUse";
        }

        (share.Provisioning, share.MountShareId, share.MountPath) = (CloudAccessProvisioning.Folder, host.Id, path);
        logger.LogWarning(LogEvents.CloudAccessMountChanged, LogMessages.CloudAccessMountChanged,
            share.Name, host.Name, path, actor.User.Username);
        return null;
    }

    /// <summary>Switches <paramref name="share"/> back to a standalone virtual share.</summary>
    public async Task<string?> UnmountAsync(UserContext actor, CloudAccessShare share)
    {
        if (!await authorization.CanManageAsync(actor, share))
            return "Web_CloudAccess_Mount_Error_NotAllowed";
        await repository.SetMountAsync(share.Id, CloudAccessProvisioning.VirtualShare, null, null);
        (share.Provisioning, share.MountShareId, share.MountPath) = (CloudAccessProvisioning.VirtualShare, null, null);
        logger.LogWarning(LogEvents.CloudAccessMountRemoved, LogMessages.CloudAccessMountRemoved,
            share.Name, actor.User.Username);
        return null;
    }

    private static bool NameTaken(string parentAbsolute, string name)
        => Directory.EnumerateFileSystemEntries(parentAbsolute)
            .Any(entry => string.Equals(Path.GetFileName(entry), name, StringComparison.OrdinalIgnoreCase));
}
