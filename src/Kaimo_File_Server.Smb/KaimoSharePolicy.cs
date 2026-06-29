using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Microsoft.Extensions.DependencyInjection;
using Smb.Auth;
using Smb.Server.Authorization;

namespace Kaimo_File_Server.Smb;

/// <summary>
/// Bridges the library's share authorization seam to Kaimo's ACL model:
///   • <see cref="IsVisible"/>  → <see cref="IAuthenticationLookup.CanListShareAsync"/> (ABE, honors hidden),
///   • <see cref="AuthorizeConnect"/> → <see cref="IAuthenticationLookup.CanAccessShareAsync"/> (TREE_CONNECT).
///
/// Connect grants full access at the share level; the fine-grained per-path ACL stays in
/// <see cref="KaimoFileStore"/> / <c>IFileService</c>, exactly as before.
/// </summary>
internal sealed class KaimoSharePolicy : IShareAccessPolicy
{
    private readonly IServiceProvider _services;

    public KaimoSharePolicy(IServiceProvider services) => _services = services;

    public bool IsVisible(ShareAccessContext context)
    {
        if (context.Share is not KaimoShare share)
            return false;

        UserContext? user = ResolveUser(context.Identity);
        if (user == null)
            return !share.IsHidden; // can't resolve the user → only non-hidden shares (safe ABE fallback)

        try
        {
            using var scope = _services.CreateScope();
            var auth = scope.ServiceProvider.GetRequiredService<IAuthenticationLookup>();
            return SmbSync.Run(() => auth.CanListShareAsync(share.ShareId, user.User.Id));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ABE] Visibility check failed for '{share.Name}': {ex.Message}");
            return false; // can't determine → hide (safe default)
        }
    }

    public ShareAccessResult AuthorizeConnect(ShareAccessContext context)
    {
        if (context.Share is not KaimoShare share)
            return ShareAccessResult.Deny();

        UserContext? user = ResolveUser(context.Identity);
        if (user == null)
            return ShareAccessResult.Deny();

        try
        {
            using var scope = _services.CreateScope();
            var auth = scope.ServiceProvider.GetRequiredService<IAuthenticationLookup>();
            // Connect-time check: ACL only, NOT the hidden flag — a hidden share must stay reachable
            // via its direct \\server\share path.
            bool ok = SmbSync.Run(() => auth.CanAccessShareAsync(share.ShareId, user.User.Id));
            return ok ? ShareAccessResult.Grant(SmbAccessMask.FullAccess) : ShareAccessResult.Deny();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ShareAccess] {context.Identity.UserName} -> {share.Name}: {ex.Message}");
            return ShareAccessResult.Deny();
        }
    }

    private UserContext? ResolveUser(SecurityIdentity identity)
    {
        if (identity.IsAnonymous || string.IsNullOrEmpty(identity.UserName))
            return null;

        UserContext? cached = KaimoUserRegistry.Lookup(identity.UserName);
        if (cached != null)
            return cached;

        // Fallback: enumeration may reach us before any file op cached the context.
        try
        {
            using var scope = _services.CreateScope();
            var auth = scope.ServiceProvider.GetRequiredService<IAuthenticationLookup>();
            UserContext? ctx = SmbSync.Run(() => auth.ResolveUserContextAsync(identity.UserName));
            if (ctx != null) KaimoUserRegistry.Register(ctx);
            return ctx;
        }
        catch
        {
            return null;
        }
    }
}
