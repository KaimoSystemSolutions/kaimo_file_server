using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories.Identity;
using Kaimo_File_Server.Core.Repositories.Shares;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Services.Management;
using Kaimo_File_Server.Infrastructure.Services.Files;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Kaimo_File_Server.Web.Controllers.Api;

/// <summary>
/// Names the home folders for client apps. Every home is a folder named by its owner's id (GUID)
/// at the root of the home-folder share; clients show the owner's username instead and keep the
/// GUID path for every request, like the web file browser (<c>FileBrowserViewModel.Homes</c>).
/// Only folders the caller may list at the share root are named (the same ACL-filtered listing
/// <c>browse/{shareId}/list</c> returns), plus the caller's own home, so no username leaks for a
/// home the caller cannot see.
/// </summary>
[Authorize]
[Route("api/v1/browse/homes")]
public sealed class HomeFoldersApiController(
    IUserContextFactory userContextFactory,
    IShareRepository shares,
    IFileServiceFactory fileServiceFactory,
    IUserRepository users) : ApiControllerBase
{
    /// <summary>
    /// <c>[{ folder, username }]</c>, ordered by username; empty while the home-folder share is not
    /// configured or disabled.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetHomes()
    {
        var user = await ResolveUserAsync(userContextFactory);
        if (user is null) return ApiUnauthorized();

        var share = (await shares.GetAllAsync()).FirstOrDefault(s => s.IsUserHomes);
        if (share is not { IsEnabled: true })
            return Ok(new List<HomeFolderDto>());

        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (user.User.HomeDirectoryEnabled)
            folders.Add(HomeDirectoryService.HomePathOf(user.User.Id));
        foreach (var entry in await ListRootAsync(share, user))
            if (entry.IsDirectory)
                folders.Add(entry.Name);

        var owners = (await users.GetAllAsync())
            .ToDictionary(u => u.Id.ToString("D"), u => u.Username, StringComparer.OrdinalIgnoreCase);
        var homes = folders
            .Where(owners.ContainsKey)
            .Select(folder => new HomeFolderDto(folder, owners[folder]))
            .OrderBy(h => h.Username, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Ok(homes);
    }

    // The ACL-filtered share root; nothing when the caller may not list it at all.
    private async Task<List<FileMetadata>> ListRootAsync(ShareDefinition share, UserContext user)
    {
        try
        {
            return await fileServiceFactory.CreateForShare(share).ListAsync(string.Empty, user);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return [];
        }
    }
}
