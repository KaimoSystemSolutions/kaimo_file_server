using Kaimo_File_Server.Core.Repositories.Identity;
using Kaimo_File_Server.Core.Repositories.Shares;
using Kaimo_File_Server.Web.Components.Search;
using Kaimo_File_Server.Web.Helpers;
using Kaimo_File_Server.Web.Services.CloudAccess;
using Microsoft.AspNetCore.Components.Authorization;

namespace Kaimo_File_Server.Web.Services.Files;

/// <summary>
/// Maps a path typed after "/" in the path bar ("Share/sub") to its route below <c>/files</c>,
/// naming shares the way the shares overview lists them: a local share (any letter case, shown
/// in its real spelling), the own home ("user"), or a standalone virtual share the user may open,
/// whose route carries its id. A local name wins over a virtual share of the same name. Virtual
/// folders are not shares; they are reached through their host share. Anything else is left as
/// typed, so the browser reports it as not found. Access is still checked when the target loads.
/// </summary>
public sealed class SharePathRouter(
    IShareRepository shares,
    CloudAccessAuthorizationService cloudAccess,
    AuthenticationStateProvider authenticationState,
    IUserContextFactory userContexts)
{
    /// <summary>Escaped route below <c>/files</c> for <paramref name="path"/> (not empty).</summary>
    public async Task<string> RouteOfAsync(string path)
    {
        var slash = path.IndexOf('/');
        var name = slash < 0 ? path : path[..slash];
        var rest = slash < 0 ? "" : path[slash..];
        return RouteSubPath.Escape(await ResolveShareAsync(name) + rest);
    }

    private async Task<string> ResolveShareAsync(string name)
    {
        if (name.Equals(SearchHitNavigation.HomeRoute, StringComparison.OrdinalIgnoreCase))
            return SearchHitNavigation.HomeRoute;

        var locals = await shares.GetAllAsync();
        var local = locals.FirstOrDefault(s => s.Name == name)
                    ?? locals.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (local is not null)
            return local.Name;

        var username = (await authenticationState.GetAuthenticationStateAsync()).User.Identity?.Name;
        var actor = string.IsNullOrEmpty(username) ? null : await userContexts.CreateByUsernameAsync(username);
        if (actor is null)
            return name;
        var virtualShare = (await cloudAccess.GetVisibleSharesAsync(actor))
            .FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return virtualShare is null ? name : $"access/{virtualShare.Id}";
    }
}
