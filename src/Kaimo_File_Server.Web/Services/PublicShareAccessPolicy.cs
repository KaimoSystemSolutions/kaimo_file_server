using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.Notifications;
using Kaimo_File_Server.Infrastructure.Configuration;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// The one policy every anonymous access to a public download link passes, shared by the
/// download endpoint and the in-page folder browser: allowed host, download links switched on,
/// link enabled / in its window / not used up, share enabled, creator enabled, and every
/// requested path inside the link root. Everything is read fresh from the store on each call.
/// <see cref="ConsumeAsync"/> atomically counts one access (download, preview);
/// <see cref="ValidateAsync"/> only checks (listing, navigating).
/// </summary>
public sealed class PublicShareAccessPolicy(
    IShareLinkRepository links,
    IShareRepository shares,
    IUserContextFactory userContexts,
    IConfigRepository config,
    LinkAccessNotificationGate notificationGate,
    INotificationPublisher? notifications = null)
{
    public Task<PublicShareAccess> ConsumeAsync(string token, string? host, IReadOnlyList<string> relativePaths)
        => CheckAsync(token, host, relativePaths, consume: true);

    public Task<PublicShareAccess> ValidateAsync(string token, string? host)
        => CheckAsync(token, host, [], consume: false);

    /// <summary>Publishes "link accessed" for the first access of a visit (see <see cref="LinkAccessNotificationGate"/>).</summary>
    public async Task NotifyAccessedAsync(ShareLink link, Guid? visitId, string fileName)
    {
        if (notifications is null || !notificationGate.TryEnter(link.Id, visitId)) return;
        await notifications.PublishAsync(NotificationEvents.LinkAccessed(
            link.Id, link.DisplayName, link.CreatedByUserId, fileName), CancellationToken.None);
    }

    private async Task<PublicShareAccess> CheckAsync(
        string token, string? host, IReadOnlyList<string> relativePaths, bool consume)
    {
        if (!await ShareLinkSettings.AllowsHostAsync(config, host))
            return new(PublicShareDenial.HostNotAllowed);

        // Download links switched off globally: open pages and issued tickets stop working too.
        if (!(await config.GetAsync(ShareLinkSettings.ConfigKey, ShareLinkSettings.Default())).AllowDownloadLinks)
            return new(PublicShareDenial.DownloadLinksDisabled);

        var link = consume ? await TryConsumeAsync(token) : await links.GetByTokenAsync(token);
        // A consumed access may have been the last one, so only a pure check tests "used up".
        if (link is null || link.Kind != ShareLinkKind.Download
            || (!consume && !link.IsCurrentlyActive(DateTime.UtcNow)))
            return new(PublicShareDenial.LinkUnavailable);

        var share = await shares.GetByIdAsync(link.ShareId);
        if (share is null || !share.IsEnabled)
            return new(PublicShareDenial.ShareUnavailable);

        // A link stops working once its creator is disabled.
        var actor = await userContexts.CreateByUserIdAsync(link.CreatedByUserId);
        if (actor is null || !actor.User.IsEnabled)
            return new(PublicShareDenial.CreatorUnavailable);

        var root = ShareRelativePath.Normalize(link.RootRelativePath);
        var paths = new List<string>(relativePaths.Count);
        foreach (var raw in relativePaths)
        {
            if (!ShareRelativePath.TryNormalizeStrict(raw, out var rel, allowRoot: false))
                return new(PublicShareDenial.InvalidPath);
            if (!IsWithin(rel, root))
                return new(PublicShareDenial.OutsideRoot);
            paths.Add(rel);
        }

        return new(PublicShareDenial.None, link, share, actor, paths);
    }

    // Atomic gate (enabled + window + not used up) plus one access consumed.
    private async Task<ShareLink?> TryConsumeAsync(string token)
    {
        try
        {
            return await links.TryConsumeAccessAsync(token);
        }
        catch (ReadOnlyDemoException)
        {
            // Read-only demo: the counter cannot be persisted, so apply the same check without consuming.
            var link = await links.GetByTokenAsync(token);
            return link is not null && link.IsCurrentlyActive(DateTime.UtcNow) ? link : null;
        }
    }

    public static bool IsWithin(string path, string root)
        => root.Length == 0
           || string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
           || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
}
