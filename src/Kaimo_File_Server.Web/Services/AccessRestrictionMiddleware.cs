using System.Security.Claims;
using Kaimo_File_Server.Infrastructure.Services;
using Kaimo_File_Server.Web.Controllers.Api;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Enforces the access restriction rules on every authenticated request: client API (bearer),
/// WebDAV (Basic, including its credential cache, and bearer) with a 403, and the Blazor hub
/// connection by downgrading it to anonymous.
/// Registered after <c>UseAuthorization</c>, where <see cref="HttpContext.User"/> is resolved
/// for every scheme. Login and token refresh check the rules themselves; running circuits are
/// re-checked by <see cref="JwtAuthenticationStateProvider"/>.
/// </summary>
public sealed class AccessRestrictionMiddleware(RequestDelegate next)
{
    public const string ErrorCode = "address_not_allowed";

    public static readonly ApiError Error =
        new(ErrorCode, "Access from this address is not allowed for this account.");

    public async Task InvokeAsync(HttpContext context, AccessRestrictionService access)
    {
        if (context.User.Identity?.IsAuthenticated == true
            && Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            && !await access.IsAllowedAsync(userId, context.Connection.RemoteIpAddress?.ToString(), context.Request.Host.Host))
        {
            // On the Blazor hub the session cookie is treated like a rejected one: the circuit
            // starts anonymous and shows the login page. A 403 would break the whole UI,
            // including the login, until the cookie expires.
            if (context.Request.Path.StartsWithSegments(WebSessionCookie.BlazorHubPath))
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity());
                await next(context);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(Error);
            return;
        }

        await next(context);
    }
}
