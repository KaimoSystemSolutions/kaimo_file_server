using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>
/// One row in the share-link admin overview: the link plus resolved display fields.
/// <paramref name="CanEdit"/> is false for a restricted row (a link of a creator the actor does
/// not outrank): its URL stays empty and it may only be disabled or deleted.
/// </summary>
public sealed record ShareLinkRow(ShareLink Link, string ShareName, string CreatedBy, string Url, bool CanEdit)
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
            if (Link.Kind == ShareLinkKind.Upload && Link.MaxTotalBytes is { } q && Link.UploadedBytes >= q) return "exhausted";
            return "active";
        }
    }
}

/// <summary>
/// Admin overview of public download and upload links. Lists the links the actor may manage (all
/// links for a global admin, otherwise only those on shares in scope — per kind, by
/// <see cref="ManagementPermission.ManageShareLinks"/> for download links and
/// <see cref="ManagementPermission.ManageUploadLinks"/> for upload links) and supports
/// enable/disable, edit and delete. Links run as their creator, so seeing the URL, editing and
/// enabling a foreign link additionally needs the actor to outrank its creator; disabling and
/// deleting need only the share right. Every mutation re-reads the stored link and re-checks the
/// freshly resolved actor.
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

    /// <summary>Whether the actor manages download links anywhere (shows the Download tab).</summary>
    public bool CanManageDownloadLinks { get; private set; }

    /// <summary>Whether the actor manages upload links anywhere (shows the Upload tab).</summary>
    public bool CanManageUploadLinks { get; private set; }
    public string? ErrorMessage { get; private set; }

    /// <summary>Why the last save / enable / disable / delete was refused (null after success).</summary>
    public string? ActionError { get; private set; }
    public List<ShareLinkRow> Links { get; private set; } = new();

    private UserContext? _actor;
    public ShareLinkSettings Settings { get; private set; } = ShareLinkSettings.Default();
    public ServerDomainSettings Domains { get; private set; } = ServerDomainSettings.Default();

    /// <summary>
    /// The address the browser currently uses (set by the page before loading); link URLs fall
    /// back to it when no default server domain is configured.
    /// </summary>
    public string? CurrentBaseUri { get; set; }

    public event Action? OnStateChanged;
    private void Notify() => OnStateChanged?.Invoke();

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var actor = _actor = await ResolveActorAsync();
            if (actor is null) { CanAccessPage = false; return; }

            CanManageDownloadLinks = await mgmtAuth.HasAnyPermissionAsync(actor, ManagementPermission.ManageShareLinks);
            CanManageUploadLinks = await mgmtAuth.HasAnyPermissionAsync(actor, ManagementPermission.ManageUploadLinks);
            CanAccessPage = CanManageDownloadLinks || CanManageUploadLinks;
            if (!CanAccessPage) return;

            Settings = await shareLinks.GetSettingsAsync();
            Domains = await shareLinks.GetDomainsAsync();

            var links = new List<ShareLink>();
            if (CanManageDownloadLinks)
                links.AddRange((await ListInScopeAsync(actor, ManagementPermission.ManageShareLinks))
                    .Where(l => l.Kind == ShareLinkKind.Download));
            if (CanManageUploadLinks)
                links.AddRange((await ListInScopeAsync(actor, ManagementPermission.ManageUploadLinks))
                    .Where(l => l.Kind == ShareLinkKind.Upload));

            Links = await BuildRowsAsync(links.OrderByDescending(l => l.CreatedAtUtc).ToList());
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

    /// <summary>
    /// Saves the editable fields of <paramref name="link"/> onto the stored link (full access
    /// required). Share, kind, target and creator always stay as stored.
    /// </summary>
    public async Task<bool> SaveAsync(ShareLink link)
    {
        ActionError = null;
        try
        {
            var stored = await AuthorizeAsync(link.Id, fullAccess: true);
            if (stored is null)
                return await RefuseAsync(link.Id);

            stored.DisplayName = link.DisplayName;
            stored.IsEnabled = link.IsEnabled;
            stored.StartsAtUtc = link.StartsAtUtc;
            stored.ExpiresAtUtc = link.ExpiresAtUtc;
            stored.MaxAccessCount = link.MaxAccessCount;
            stored.MaxBytesPerSecond = link.MaxBytesPerSecond;
            stored.PasswordHash = link.PasswordHash;
            if (stored.Kind == ShareLinkKind.Upload)
            {
                stored.MaxFileSizeBytes = link.MaxFileSizeBytes;
                stored.MaxTotalBytes = link.MaxTotalBytes;
                stored.AllowedExtensions = link.AllowedExtensions;
            }
            await repo.UpdateAsync(stored);
            await ReloadRowAsync(link.Id);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save share link {Id}", link.Id);
            await TryReloadRowAsync(link.Id);
            return false;
        }
    }

    /// <summary>Disabling needs only the share right; enabling needs full access.</summary>
    public async Task<bool> SetEnabledAsync(Guid id, bool enabled)
    {
        ActionError = null;
        try
        {
            var stored = await AuthorizeAsync(id, fullAccess: enabled);
            if (stored is null)
                return await RefuseAsync(id);

            stored.IsEnabled = enabled;
            await repo.UpdateAsync(stored);
            await ReloadRowAsync(id);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to change share link {Id}", id);
            return false;
        }
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        ActionError = null;
        try
        {
            if (await AuthorizeAsync(id, fullAccess: false) is null)
                return await RefuseAsync(id);

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

    public string BuildUrl(ShareLink link) => shareLinks.BuildUrl(link, Domains, CurrentBaseUri);

    private async Task<List<ShareLink>> ListInScopeAsync(UserContext actor, ManagementPermission permission)
    {
        var scope = await mgmtAuth.GetAuthorizedShareIdsAnyAsync(actor, permission);
        return scope.IsUnrestricted
            ? await repo.ListAllAsync()
            : await repo.ListForSharesAsync(scope.ScopeIds);
    }

    /// <summary>
    /// Re-reads the stored link (never trusting the row object) and re-checks the freshly
    /// resolved actor: the share right for the link's kind and, for <paramref name="fullAccess"/>,
    /// the creator rule. Null when refused or gone.
    /// </summary>
    private async Task<ShareLink?> AuthorizeAsync(Guid id, bool fullAccess)
    {
        var actor = await ResolveActorAsync();
        var stored = await repo.GetByIdAsync(id);
        if (actor is null || stored is null
            || !await mgmtAuth.CanManageShareAsync(actor, stored.ShareId, ShareLinkService.PermissionFor(stored.Kind)))
            return null;
        return !fullAccess || await HasFullAccessAsync(actor, stored.CreatedByUserId) ? stored : null;
    }

    private async Task<bool> HasFullAccessAsync(UserContext actor, Guid creatorId)
        => creatorId == actor.User.Id || await mgmtAuth.OutranksUserAsync(actor, creatorId);

    /// <summary>Records the refusal and restores the row from the store (the page may have edited it).</summary>
    private async Task<bool> RefuseAsync(Guid id)
    {
        ActionError = Core.Language.Resources.Web_ShareLinks_NotPermitted;
        await ReloadRowAsync(id);
        return false;
    }

    /// <summary>
    /// Best effort after a failed write: the page edited the row object in place, so show the
    /// stored state again; when the store is unreachable too, the error is all that is left.
    /// </summary>
    private async Task TryReloadRowAsync(Guid id)
    {
        try
        {
            await ReloadRowAsync(id);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not reload share link {Id} after a failed save", id);
        }
    }

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
        var fullAccess = new Dictionary<Guid, bool>();

        using (mgmtAuth.BeginEvaluationScope())
        {
            foreach (var creatorId in links.Select(l => l.CreatedByUserId).Distinct())
                fullAccess[creatorId] = _actor is not null && await HasFullAccessAsync(_actor, creatorId);
        }

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
            .Select(l =>
            {
                var canEdit = fullAccess.GetValueOrDefault(l.CreatedByUserId);
                return new ShareLinkRow(
                    l,
                    shareNames.GetValueOrDefault(l.ShareId, ""),
                    creatorNames.GetValueOrDefault(l.CreatedByUserId, ""),
                    // The token is never revealed for a restricted row.
                    canEdit ? shareLinks.BuildUrl(l, Domains, CurrentBaseUri) : string.Empty,
                    canEdit);
            })
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
