using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Services;
using Microsoft.AspNetCore.Components.Authorization;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>One home folder in the metadata overview. <see cref="User"/> is null for an orphaned folder.</summary>
public sealed record HomeFolderRow(Guid UserId, User? User, long SizeBytes, IReadOnlyList<ShareLink> Links)
{
    public bool IsEnabled => User?.HomeDirectoryEnabled == true;
}

/// <summary>
/// Metadata-only overview of all home folders (<c>/files/users</c>), gated by
/// <see cref="ManagementPermission.ManageHomes"/>. It deliberately never lists folder content:
/// sizes are read from disk and links from the link table, while the holder of this permission
/// has no ACL entry in anyone else's home.
/// </summary>
public sealed class HomesAdminViewModel(
    HomeDirectoryService homes,
    IUserRepository users,
    IShareLinkRepository shareLinks,
    IUserContextFactory userContexts,
    IManagementAuthService mgmtAuth,
    AuthenticationStateProvider authState,
    ILogger<HomesAdminViewModel> logger)
{
    /// <summary>Display name of the overview entry in the share list.</summary>
    public static string DisplayName => Resources.ResourceManager.GetString("Web_Homes_DisplayName") ?? "users";

    public bool IsLoading { get; private set; }
    public bool CanAccessPage { get; private set; }
    public bool IsConfigured { get; private set; }

    /// <summary>False while home folders are switched off for everyone (Settings → Storage).</summary>
    public bool IsGloballyEnabled { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string? SuccessMessage { get; private set; }
    public List<HomeFolderRow> Rows { get; private set; } = [];
    public Guid? SelectedUserId { get; set; }

    public HomeFolderRow? Selected => Rows.FirstOrDefault(r => r.UserId == SelectedUserId);

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var name = (await authState.GetAuthenticationStateAsync()).User.Identity?.Name;
            var actor = string.IsNullOrEmpty(name) ? null : await userContexts.CreateByUsernameAsync(name);
            CanAccessPage = actor is not null
                && await mgmtAuth.HasGlobalPermissionAsync(actor, ManagementPermission.ManageHomes);
            if (!CanAccessPage)
                return;

            var share = await homes.GetHomesShareAsync();
            IsConfigured = share is not null;
            IsGloballyEnabled = share?.IsEnabled == true;
            if (share is null)
            {
                Rows = [];
                return;
            }

            var usersById = (await users.GetAllAsync()).ToDictionary(u => u.Id);
            var linksByHome = (await shareLinks.ListForSharesAsync([share.Id]))
                .GroupBy(l => ShareRelativePath.Normalize(l.RootRelativePath).Split('/')[0], StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<ShareLink>)g.ToList(), StringComparer.OrdinalIgnoreCase);

            // ponytail: sizes are walked synchronously on every load; move to a background
            // refresh (like the share overview) if installations with very large homes appear.
            Rows = await Task.Run(() => Directory.EnumerateDirectories(share.Path)
                .Select(dir => Guid.TryParse(Path.GetFileName(dir), out var id) ? (id, dir) : (Guid.Empty, dir))
                .Where(x => x.Item1 != Guid.Empty)
                .Select(x => new HomeFolderRow(
                    x.Item1,
                    usersById.GetValueOrDefault(x.Item1),
                    SizeOf(x.dir),
                    linksByHome.GetValueOrDefault(HomeDirectoryService.HomePathOf(x.Item1)) ?? []))
                .OrderBy(r => r.User is null)
                .ThenBy(r => r.User?.Name ?? r.UserId.ToString(), StringComparer.CurrentCultureIgnoreCase)
                .ToList());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Loading the home-folder overview failed");
            ErrorMessage = Resources.ResourceManager.GetString("Web_Homes_LoadFailed") ?? "Home folders could not be loaded.";
            Rows = [];
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Enables or disables the selected user's home, then reloads.</summary>
    public async Task SetEnabledAsync(Guid userId, bool enabled)
    {
        if (!CanAccessPage || !IsGloballyEnabled)
            return;
        try
        {
            await homes.SetEnabledAsync(userId, enabled);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Changing the home folder of {UserId} failed", userId);
            ErrorMessage = Resources.ResourceManager.GetString("Web_Homes_SaveFailed") ?? "The home folder could not be changed.";
            return;
        }
        await LoadAsync();
    }

    /// <summary>
    /// Permanently deletes a home folder (content, links, versions). A user who still has an
    /// enabled home gets a fresh empty one; an orphaned folder disappears from the list.
    /// </summary>
    public async Task<bool> DeleteHomeAsync(Guid userId)
    {
        if (!CanAccessPage)
            return false;
        SuccessMessage = null;
        try
        {
            await homes.DeleteHomeAsync(userId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Deleting the home folder of {UserId} failed", userId);
            ErrorMessage = Resources.ResourceManager.GetString("Web_Homes_DeleteFailed") ?? "The home folder could not be deleted.";
            return false;
        }
        await LoadAsync();
        SuccessMessage = Resources.ResourceManager.GetString("Web_Homes_Deleted") ?? "Home folder deleted.";
        return true;
    }

    private static long SizeOf(string dir)
        => new DirectoryInfo(dir)
            .EnumerateFiles("*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            })
            .Sum(f => f.Length);
}
