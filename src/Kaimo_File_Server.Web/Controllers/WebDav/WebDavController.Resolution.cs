using System.Security.Claims;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Kaimo_File_Server.Web.Controllers.WebDav;

public partial class WebDavController
{
    private sealed record Target(UserContext User, ShareDefinition Share, IFileService Fs, string Path);

    private async Task<(Target? target, IActionResult? error)> ResolveAsync(bool allowRoot = true)
    {
        var user = await ResolveUserAsync();
        if (user is null) return (null, Challenge());

        if (!WebDavPathResolver.TrySplit(Request.Path.Value, out var shareName, out var relativePath)
            || shareName.Length == 0)
            return (null, new StatusCodeResult(WebDavPathResolver.IsMalformedItemPath(Request.Path.Value)
                ? StatusCodes.Status400BadRequest
                : StatusCodes.Status404NotFound));

        if (!allowRoot && relativePath.Length == 0)
            return (null, new StatusCodeResult(StatusCodes.Status403Forbidden));

        var share = await GetVisibleShareAsync(shareName);
        if (share is null) return (null, new StatusCodeResult(StatusCodes.Status404NotFound));

        var fs = _fileServiceFactory.CreateForShare(share);
        return (new Target(user, share, fs, relativePath), null);
    }

    /// <summary>
    /// Rebuilds the caller's <see cref="UserContext"/> from the authenticated
    /// principal (Basic or Bearer), re-checking existence and the enabled flag, and
    /// — for device-scoped bearer tokens — that the device is still active. Returns
    /// <c>null</c> when the account is gone/disabled or the device was revoked.
    /// </summary>
    private async Task<UserContext?> ResolveUserAsync()
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return null;

        if (Guid.TryParse(User.FindFirstValue(JwtTokenService.DeviceIdClaim), out var deviceId))
        {
            var devices = HttpContext.RequestServices.GetRequiredService<ISyncDeviceRepository>();
            var device = await devices.GetByIdAsync(deviceId);
            if (device is null || !device.IsActive)
                return null;
        }

        var context = await _userContextFactory.CreateByUserIdAsync(userId);
        return context is { User.IsEnabled: true } ? context : null;
    }

    /// <summary>
    /// A new resource must get a name Windows can use. Unlike the REST API, WebDAV does not
    /// rename: clients read the resource back at the URL they wrote. Existing entries with such
    /// names stay readable, writable and deletable.
    /// </summary>
    private static bool IsCreatableName(string path)
        => WindowsFileNameHelper.IsValid(ShareRelativePath.GetFileName(path));

    /// <summary>A share addressable over WebDAV: enabled and known. Hidden shares stay reachable by direct URL.</summary>
    private async Task<ShareDefinition?> GetVisibleShareAsync(string name)
    {
        var share = await _shares.GetByNameAsync(name);
        return share is { IsEnabled: true } ? share : null;
    }

    /// <summary>Maps the file service's exceptions to WebDAV status codes (same mapping as the REST browse API).</summary>
    private async Task<IActionResult> GuardAsync(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (UnauthorizedAccessException) { return new StatusCodeResult(StatusCodes.Status403Forbidden); }
        catch (FileNotFoundException) { return new StatusCodeResult(StatusCodes.Status404NotFound); }
        catch (DirectoryNotFoundException) { return new StatusCodeResult(StatusCodes.Status404NotFound); }
        catch (KeyNotFoundException) { return new StatusCodeResult(StatusCodes.Status404NotFound); }
        catch (InvalidOperationException) { return new StatusCodeResult(StatusCodes.Status409Conflict); }
        // A storage-level failure (e.g. the target directory is not writable by the
        // container's user) is not the client's fault — report 409 rather than letting
        // it surface as an unhandled 500.
        catch (IOException) { return new StatusCodeResult(StatusCodes.Status409Conflict); }
    }

    /// <summary>
    /// Metadata for an existing item, or <c>null</c> when it does not exist for this user.
    /// Same answer as a lookup in the parent listing (which is how a WebDAV client reaches the
    /// item), without listing the parent; the entry carries the real size/mtime an
    /// <c>If-Match</c> needs. The share root always exists.
    /// </summary>
    internal static Task<FileMetadata?> FindAsync(IFileService fs, string path, UserContext user)
        => fs.FindEntryAsync(path, user);

    /// <summary>True when the parent collection of <paramref name="path"/> exists (the share root always does).</summary>
    internal static async Task<bool> ParentExistsAsync(IFileService fs, string path, UserContext user)
    {
        var parent = ShareRelativePath.GetParent(path);
        return parent.Length == 0 || await FindAsync(fs, parent, user) is { IsDirectory: true };
    }
}
