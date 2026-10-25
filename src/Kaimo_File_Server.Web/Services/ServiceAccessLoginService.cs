using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Refuses a successful credential check when the file service it is made for is switched
/// off (<see cref="ServiceAccessGate"/>). The service is read from the request path, like
/// the security monitor's channel: <c>/api</c> is the client app, <c>/dav</c> is WebDAV and
/// everything else (Blazor circuit) the web login. Wraps the credential service so all
/// transports are covered without touching their call sites; they show
/// <see cref="LoginOutcome.ServiceDisabled"/> exactly like a wrong password.
/// </summary>
public sealed class ServiceAccessLoginService(
    ILoginService inner,
    ServiceAccessGate serviceAccess,
    IHttpContextAccessor http) : ILoginService
{
    public async Task<LoginResult> AuthenticateAsync(string username, string password, string? remoteAddress = null, string? host = null)
    {
        var result = await inner.AuthenticateAsync(username, password, remoteAddress, host);
        if (result.Outcome != LoginOutcome.Success || result.UserContext is null)
            return result;

        var service = SecurityMonitorMiddleware.ChannelOf(http.HttpContext?.Request.Path) switch
        {
            SecurityChannel.Api => UserServiceAccess.App,
            SecurityChannel.WebDav => UserServiceAccess.WebDav,
            _ => UserServiceAccess.Web,
        };
        return await serviceAccess.IsAllowedAsync(result.UserContext.User, service)
            ? result
            : LoginResult.ServiceDisabled;
    }
}
