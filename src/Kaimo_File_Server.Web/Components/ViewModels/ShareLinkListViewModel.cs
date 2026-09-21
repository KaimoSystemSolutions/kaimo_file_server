using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>One row in the share-link admin overview: the link plus resolved display fields.</summary>
public sealed record ShareLinkRow(ShareLink Link, string ShareName, string CreatedBy, string Url)
{
    /// <summary>Coarse state key for the status badge: active / disabled / expired / pending / exhausted.</summary>
    public string StatusKey
    {
        get
        {
            var now = DateTime.UtcNow;
            if (!Link.IsEnabled) return "disabled";
            if (Link.ExpiresAtUtc is { } e && now > e) return "expired";
            if (Link.StartsAtUtc is { } s && now < s) return "pending";
            if (Link.MaxAccessCount is { } m && Link.AccessCount >= m) return "exhausted";
            return "active";
        }
    }
}

/// <summary>
/// Admin overview of public share links. Lists the links the actor may manage (all links for a
/// global admin, otherwise only those on shares in scope) and supports enable/disable, edit and
/// delete. Gated by <see cref="ManagementPermission.ManageShareLinks"/>.
/// </summary>
public sealed class ShareLinkListViewModel(
    IShareLinkRepository repo,
    IShareRepository shares,
    IUserRepository users,
    IManagementAuthService mgmtAuth,
    IUserContextFactory userContexts,
    AuthenticationStateProvider authState,
    ShareLinkService shareLinks,
    ILogger<ShareLinkListViewModel> logger)
{
    public bool IsLoading { get; private set; }
    public bool CanAccessPage { get; private set; }
    public string? ErrorMessage { get; private set; }
    public List<ShareLinkRow> Links { get; private set; } = new();
    public ShareLinkSettings Settings { get; private set; } = ShareLinkSettings.Default();

    public event Action? OnStateChanged;
    private void Notify() => OnStateChanged?.Invoke();

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var actor = await ResolveActorAsync();
            if (actor is null) { CanAccessPage = false; return; }

            CanAccessPage = await mgmtAuth.HasAnyPermissionAsync(actor, ManagementPermission.ManageShareLinks);
            if (!CanAccessPage) return;

            Settings = await shareLinks.GetSettingsAsync();

            var scope = await mgmtAuth.GetAuthorizedShareIdsAnyAsync(actor, ManagementPermission.ManageShareLinks);
            var links = scope.IsUnrestricted
                ? await repo.ListAllAsync()
                : await repo.ListForSharesAsync(scope.ScopeIds);

            Links = await BuildRowsAsync(links);
        }
        catch (Exception ex)
        {
            ErrorMessage = Core.Language.Resources.Web_Error_LoadFilesFailed;
            logger.LogError(ex, "Failed to load share links");
        }
        finally
        {
            IsLoading = false;
            Notify();
        }
    }

    public async Task<bool> SaveAsync(ShareLink link)
    {
        try
        {
            await repo.UpdateAsync(link);
            await ReloadRowAsync(link.Id);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save share link {Id}", link.Id);
            return false;
        }
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        try
        {
            await repo.DeleteAsync(id);
            Links.RemoveAll(r => r.Link.Id == id);
            Notify();
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete share link {Id}", id);
            return false;
        }
    }

    public string BuildUrl(ShareLink link) => shareLinks.BuildUrl(link, Settings);

    private async Task ReloadRowAsync(Guid id)
    {
        var fresh = await repo.GetByIdAsync(id);
        if (fresh is null) { Links.RemoveAll(r => r.Link.Id == id); Notify(); return; }

        var rebuilt = (await BuildRowsAsync(new[] { fresh })).FirstOrDefault();
        var index = Links.FindIndex(r => r.Link.Id == id);
        if (rebuilt is not null && index >= 0) Links[index] = rebuilt;
        Notify();
    }

    private async Task<List<ShareLinkRow>> BuildRowsAsync(IReadOnlyCollection<ShareLink> links)
    {
        var shareNames = new Dictionary<Guid, string>();
        var creatorNames = new Dictionary<Guid, string>();

        foreach (var shareId in links.Select(l => l.ShareId).Distinct())
        {
            if (shareNames.ContainsKey(shareId)) continue;
            var share = await shares.GetByIdAsync(shareId);
            shareNames[shareId] = share?.Name ?? shareId.ToString()[..8];
        }

        foreach (var userId in links.Select(l => l.CreatedByUserId).Distinct())
        {
            if (creatorNames.ContainsKey(userId)) continue;
            var user = await users.GetByIdAsync(userId);
            creatorNames[userId] = user is null
                ? userId.ToString()[..8]
                : (string.IsNullOrWhiteSpace(user.Name) ? user.Username : user.Name);
        }

        return links
            .Select(l => new ShareLinkRow(
                l,
                shareNames.GetValueOrDefault(l.ShareId, ""),
                creatorNames.GetValueOrDefault(l.CreatedByUserId, ""),
                shareLinks.BuildUrl(l, Settings)))
            .ToList();
    }

    private async Task<UserContext?> ResolveActorAsync()
    {
        var state = await authState.GetAuthenticationStateAsync();
        var username = state.User.Identity?.Name;
        if (string.IsNullOrEmpty(username)) return null;
        return await userContexts.CreateByUsernameAsync(username);
    }
}
