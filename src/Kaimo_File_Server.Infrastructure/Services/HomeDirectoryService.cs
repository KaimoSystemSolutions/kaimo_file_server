using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services.File;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Services;

/// <summary>Outcome of <see cref="HomeDirectoryService.ConfigureAsync"/>.</summary>
public enum HomeConfigureResult
{
    Ok,
    AlreadyConfigured,
    NameTaken,
    DestinationIsFile,
}

/// <summary>
/// Provisions and revokes the per-user home folders.
///
/// All homes live in one system share named <see cref="ShareName"/> whose root holds
/// one folder per user, named after the user id. Access is expressed purely through ACL
/// entries, so every transport (Web, REST, WebDAV, SMB) enforces it without special code:
/// <list type="bullet">
///   <item>share root: <c>ReadAll</c> on the root itself only, per user with an enabled home
///     (SMB tree-connect and traversal need it; listings are ACL-filtered, so each user
///     only ever sees their own folder);</item>
///   <item><c>&lt;userId&gt;</c> itself: read/write without <c>Delete</c>, so the home cannot
///     be removed over WebDAV/SMB;</item>
///   <item>everything below: read/write. Never <c>AdminAll</c>, so a user cannot hand out
///     access to their home.</item>
/// </list>
/// Administrators receive no entry at all: they only see metadata. Department defaults never
/// apply to this share (see <c>AclService.ResolveDepartmentDefaultAsync</c>).
/// </summary>
public sealed class HomeDirectoryService(
    IShareRepository shares,
    IUserRepository users,
    IFileMetadataRepository metadata,
    IAclRepository acls,
    IFileVersionService versions,
    IShareLinkRepository shareLinks,
    IFileChangeLogRepository changeLog,
    ILogger<HomeDirectoryService> logger)
{
    /// <summary>Name of the home-folder share, identical on every transport.</summary>
    public const string ShareName = "users";

    private const FilePermission OwnerPermissions = FilePermission.ReadAll | FilePermission.WriteAll;
    private const FilePermission HomeFolderPermissions = OwnerPermissions & ~FilePermission.Delete;
    private const AclInheritance BelowHomeFolder =
        AclInheritance.SubFolders | AclInheritance.SubFiles | AclInheritance.AllDescendants;

    // 2775, as sync-shares.sh applies to share roots: every SMB user has their own UID and
    // writes through the shared storage group, so these directories need group rwx + setgid
    // whatever the umask of the creating process (the dev launch runs with 0022).
    private const UnixFileMode SharedDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute | UnixFileMode.SetGroup;

    /// <summary>Share-relative path of a user's home folder.</summary>
    public static string HomePathOf(Guid userId) => userId.ToString();

    /// <summary>The home-folder share, or <c>null</c> while the feature is not configured.</summary>
    public async Task<ShareDefinition?> GetHomesShareAsync()
        => (await shares.GetAllAsync()).FirstOrDefault(s => s.IsUserHomes);

    /// <summary>
    /// Whether <paramref name="user"/> currently has a usable home, i.e. the feature is
    /// configured and the home is enabled for them. Performs no writes.
    /// </summary>
    public async Task<ShareDefinition?> GetShareForUserAsync(User user)
        => user.HomeDirectoryEnabled && await GetHomesShareAsync() is { IsEnabled: true } share
            ? share
            : null;

    /// <summary>
    /// Creates the home-folder share on <paramref name="poolPath"/> and provisions a home for
    /// every user with an enabled home. An existing <c>users</c> directory on the pool is
    /// adopted, so homes re-attach to their users after a database reset.
    /// </summary>
    public async Task<HomeConfigureResult> ConfigureAsync(string poolPath, Guid actorUserId)
    {
        if (await GetHomesShareAsync() is not null)
            return HomeConfigureResult.AlreadyConfigured;
        if (await shares.GetByNameAsync(ShareName) is not null)
            return HomeConfigureResult.NameTaken;

        var path = Path.Combine(poolPath, ShareName);
        if (File.Exists(path))
            return HomeConfigureResult.DestinationIsFile;

        CreateSharedDirectory(path);
        var share = new ShareDefinition(
            ShareName, path, WellKnownGUIDs.DEPARTMENT_GLOBAL,
            isEnabled: true, isShareHidden: false, isRecycleEnabled: false)
        {
            IsUserHomes = true
        };
        await shares.CreateAsync(share);
        await metadata.GetOrCreateAsync("", isDirectory: true, userId: actorUserId, shareId: share.Id);

        logger.LogInformation("Home-folder share created at '{Path}'", path);
        await BackfillAsync();
        return HomeConfigureResult.Ok;
    }

    /// <summary>
    /// Idempotently creates the user's home folder and grants the owner entries.
    /// Returns the share and the home's share-relative path, or <c>null</c> when the user
    /// has no usable home.
    /// </summary>
    public async Task<(ShareDefinition Share, string Path)?> EnsureHomeAsync(User user)
    {
        if (await GetShareForUserAsync(user) is not { } share)
            return null;

        var home = HomePathOf(user.Id);
        // Also repairs the mode of homes created before, so it runs on every call.
        CreateSharedDirectory(Path.Combine(share.Path, home));

        var rootMeta = await metadata.GetOrCreateAsync("", true, user.Id, share.Id);
        if (!await HasEntryAsync(rootMeta.Id, user.Id))
            await acls.AddAsync(new AccessEntry(
                user.Id, AclEntryType.Allow, FilePermission.ReadAll, AclInheritance.ThisFolder)
            { FileMetadataId = rootMeta.Id });

        var homeMeta = await metadata.GetOrCreateAsync(home, true, user.Id, share.Id);
        if (!await HasEntryAsync(homeMeta.Id, user.Id))
        {
            await acls.AddAsync(new AccessEntry(
                user.Id, AclEntryType.Allow, HomeFolderPermissions, AclInheritance.ThisFolder)
            { FileMetadataId = homeMeta.Id });
            await acls.AddAsync(new AccessEntry(
                user.Id, AclEntryType.Allow, OwnerPermissions, BelowHomeFolder)
            { FileMetadataId = homeMeta.Id });
        }

        return (share, home);
    }

    /// <summary>
    /// Enables or disables a user's home. Disabling removes every entry the user holds in the
    /// share (root and home folder), so the home becomes unreachable and invisible on every
    /// transport; its files are kept on disk.
    /// </summary>
    public async Task SetEnabledAsync(Guid userId, bool enabled)
    {
        await users.UpdateHomeDirectoryEnabledAsync(userId, enabled);

        if (enabled)
        {
            if (await users.GetByIdAsync(userId) is { } user)
                await EnsureHomeAsync(user);
            return;
        }

        if (await GetHomesShareAsync() is not { } share)
            return;

        foreach (var path in new[] { "", HomePathOf(userId) })
        {
            var meta = await metadata.GetOrCreateAsync(path, true, userId, share.Id);
            foreach (var entry in await acls.GetByFileMetadataIdAsync(meta.Id))
                if (entry.PrincipalId == userId)
                    await acls.DeleteAsync(entry.Id);
        }
    }

    /// <summary>
    /// Turns home folders off (or back on) for every user at once by disabling the share:
    /// every access path already refuses a disabled share and SMB drops it from the registry.
    /// Per-user settings and files are kept, so re-enabling restores the previous state.
    /// </summary>
    public async Task SetGloballyEnabledAsync(bool enabled)
    {
        if (await GetHomesShareAsync() is not { } share || share.IsEnabled == enabled)
            return;

        share.IsEnabled = enabled;
        await shares.UpdateAsync(share);
        logger.LogInformation("Home folders {State} for all users", enabled ? "enabled" : "disabled");

        if (enabled)
            await BackfillAsync();
    }

    /// <summary>
    /// Permanently deletes a home folder and everything tied to it: its public links, files,
    /// metadata/ACL rows, versions, and the user's entry on the share root. A change-log entry
    /// lets sync clients and the search index drop the content. If the user still exists with
    /// an enabled home, a fresh empty home is provisioned right away.
    /// </summary>
    public async Task<bool> DeleteHomeAsync(Guid userId)
    {
        if (await GetHomesShareAsync() is not { } share)
            return false;

        var home = HomePathOf(userId);

        // Links first, so nothing keeps serving the content while it is being removed.
        foreach (var link in await shareLinks.ListForSharesAsync([share.Id]))
        {
            var root = ShareRelativePath.Normalize(link.RootRelativePath);
            if (root.Equals(home, StringComparison.OrdinalIgnoreCase)
                || root.StartsWith(home + "/", StringComparison.OrdinalIgnoreCase))
                await shareLinks.DeleteAsync(link.Id);
        }

        var dir = Path.Combine(share.Path, home);
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);

        // Removes the home's metadata subtree; its ACL rows go with it (database cascade).
        await acls.DeleteFileMetadataPathsAsync(share.Id, home);
        var rootMeta = await metadata.GetOrCreateAsync("", true, userId, share.Id);
        foreach (var entry in await acls.GetByFileMetadataIdAsync(rootMeta.Id))
            if (entry.PrincipalId == userId)
                await acls.DeleteAsync(entry.Id);

        await versions.DeletePathAsync(share.Id, home);
        await changeLog.AppendAsync(new FileChangeLogEntry
        {
            ShareId = share.Id,
            ChangeType = FileChangeType.Deleted,
            Path = home,
            IsDirectory = true,
            CreatedAtUtc = DateTime.UtcNow,
        });
        logger.LogInformation("Home folder of user {UserId} deleted", userId);

        if (await users.GetByIdAsync(userId) is { } user)
            await EnsureHomeAsync(user);
        return true;
    }

    /// <summary>Provisions the home of every user with an enabled home. Safe to run on every startup.</summary>
    public async Task BackfillAsync()
    {
        if (await GetHomesShareAsync() is not { } share)
            return;

        // The share is listed on SMB/WebDAV (each user only sees their own folder inside).
        // Share management cannot edit it, so heal a hidden flag from an earlier setup here.
        if (share.IsShareHidden)
        {
            share.IsShareHidden = false;
            await shares.UpdateAsync(share);
        }

        foreach (var user in await users.GetAllAsync())
        {
            try
            {
                await EnsureHomeAsync(user);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not provision the home folder of user {UserId}", user.Id);
            }
        }
    }

    private void CreateSharedDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            File.SetUnixFileMode(path, SharedDirectoryMode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only the owner may change the mode; a directory created by another process
            // keeps its mode, which Samba's entrypoint normalizes on its next start.
            logger.LogWarning(ex, "Could not make '{Path}' group-writable", path);
        }
    }

    private async Task<bool> HasEntryAsync(Guid fileMetadataId, Guid userId)
        => (await acls.GetByFileMetadataIdAsync(fileMetadataId)).Any(e => e.PrincipalId == userId);
}
