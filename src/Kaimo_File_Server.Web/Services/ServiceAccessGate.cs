using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Web.Controllers.WebDav;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Decides whether a user may use a file service (client app, web login, WebDAV; SMB is
/// checked in the SMB bridge). A service must be switched on globally and in the user's
/// <see cref="User.ServiceAccess"/>. Members of the Admins group always keep the web login,
/// even when it is off globally, so administrators cannot lock themselves out.
/// </summary>
public sealed class ServiceAccessGate(IConfigRepository config, IUserRepository users)
{
    /// <summary>Config key of the flags applied to newly created users.</summary>
    public const string DefaultAccessKey = "users.defaultServiceAccess";

    /// <summary>Config key of the global on/off switch of <paramref name="service"/>.</summary>
    public static string GlobalKey(UserServiceAccess service) => service switch
    {
        UserServiceAccess.App => DataServiceKeys.EnabledKey("app"),
        UserServiceAccess.Web => DataServiceKeys.EnabledKey("web"),
        UserServiceAccess.Smb => DataServiceKeys.EnabledKey("smb"),
        UserServiceAccess.WebDav => WebDavOptions.EnabledKey,
        _ => throw new ArgumentOutOfRangeException(nameof(service), service, "Exactly one service expected."),
    };

    /// <summary>Global default when the switch was never saved (WebDAV is opt-in).</summary>
    public static bool GlobalFallback(UserServiceAccess service) => service != UserServiceAccess.WebDav;

    public Task<bool> IsGloballyEnabledAsync(UserServiceAccess service)
        => config.GetBoolAsync(GlobalKey(service), GlobalFallback(service));

    public async Task<bool> IsAllowedAsync(User user, UserServiceAccess service)
    {
        if (user.ServiceAccess.HasFlag(service) && await IsGloballyEnabledAsync(service))
            return true;
        // Only looked up when the regular check fails, so the common path costs no query.
        return service == UserServiceAccess.Web && await IsAdminAsync(user.Id);
    }

    /// <summary>Whether the user belongs to the Admins group (exempt from losing the web login).</summary>
    public async Task<bool> IsAdminAsync(Guid userId)
        => (await users.GetGroupsForUserAsync(userId)).Any(g => g.Id == WellKnownGUIDs.GROUP_ADMINS);

    /// <summary>The flags given to a newly created user.</summary>
    public async Task<UserServiceAccess> GetDefaultAccessAsync()
        => (UserServiceAccess)await config.GetIntAsync(DefaultAccessKey, (int)UserServiceAccess.All) & UserServiceAccess.All;
}
