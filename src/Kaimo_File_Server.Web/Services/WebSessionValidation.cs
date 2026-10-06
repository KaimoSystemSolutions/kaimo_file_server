using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Kaimo_File_Server.Core.Repositories;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Post-signature checks for the web session token (the JWT carried in the
/// <see cref="WebSessionCookie"/>). The counterpart of <see cref="BearerTokenValidation"/>:
/// a valid signature alone is not enough — the token must be a web (not device-scoped)
/// token, unexpired, not signed out, and its account enabled with the current security
/// stamp. Used both when the Blazor connection is established and by the periodic
/// revalidation in <see cref="JwtAuthenticationStateProvider"/>.
/// </summary>
public static class WebSessionValidation
{
    public static async Task ValidateAsync(TokenValidatedContext context)
    {
        var services = context.HttpContext.RequestServices;
        string? failure = await CheckAsync(
            context.Principal,
            services.GetRequiredService<IUserRepository>(),
            services.GetRequiredService<IRevokedWebTokenRepository>());
        if (failure is not null)
            context.Fail(failure);
    }

    /// <summary>Returns <c>null</c> when the session is acceptable, otherwise the rejection reason.</summary>
    internal static async Task<string?> CheckAsync(
        ClaimsPrincipal? principal, IUserRepository users, IRevokedWebTokenRepository revokedTokens)
    {
        if (principal is null)
            return "No principal.";

        // Device tokens belong to the client API; they must never act as a browser session.
        if (principal.FindFirst(JwtTokenService.DeviceIdClaim) is not null)
            return "Device-scoped tokens are not accepted as web sessions.";

        if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return "Malformed subject.";

        // A circuit outlives the check at connect time; the periodic check ends it at expiry.
        if (long.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Exp), out var exp)
            && DateTimeOffset.FromUnixTimeSeconds(exp) <= DateTimeOffset.UtcNow)
            return "Token expired.";

        var user = await users.GetByIdAsync(userId);
        if (user is not { IsEnabled: true })
            return "Account disabled or removed.";

        if (!JwtTokenService.HasCurrentSecurityStamp(principal, user))
            return "Token was issued before the account's credentials changed.";

        var jti = principal.FindFirstValue(JwtRegisteredClaimNames.Jti);
        if (!string.IsNullOrEmpty(jti) && await revokedTokens.IsRevokedAsync(jti))
            return "Token was signed out.";

        return null;
    }
}
