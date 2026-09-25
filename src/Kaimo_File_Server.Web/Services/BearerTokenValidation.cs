using System.Security.Claims;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Post-signature checks for every JWT presented as a bearer token (client API and
/// WebDAV). A valid signature alone is not enough: the token must be a device-scoped
/// client-API token (the web login token is not accepted here), its device must still
/// be active, its account enabled, and its security stamp current — so revoking a
/// device, disabling an account or changing the password takes effect immediately.
/// </summary>
public static class BearerTokenValidation
{
    public static async Task ValidateAsync(TokenValidatedContext context)
    {
        var services = context.HttpContext.RequestServices;
        string? failure = await CheckAsync(
            context.Principal,
            services.GetRequiredService<IUserRepository>(),
            services.GetRequiredService<ISyncDeviceRepository>());
        if (failure is not null)
            context.Fail(failure);
    }

    /// <summary>Returns <c>null</c> when the principal is acceptable, otherwise the rejection reason.</summary>
    internal static async Task<string?> CheckAsync(
        ClaimsPrincipal? principal, IUserRepository users, ISyncDeviceRepository devices)
    {
        if (principal is null)
            return "No principal.";

        if (!Guid.TryParse(principal.FindFirstValue(JwtTokenService.DeviceIdClaim), out var deviceId))
            return "Only device-scoped client tokens are accepted as bearer tokens.";

        if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return "Malformed subject.";

        var device = await devices.GetByIdAsync(deviceId);
        if (device is null || !device.IsActive || device.UserId != userId)
            return "Device revoked.";

        var user = await users.GetByIdAsync(userId);
        if (user is not { IsEnabled: true })
            return "Account disabled or removed.";

        if (!JwtTokenService.HasCurrentSecurityStamp(principal, user))
            return "Token was issued before the account's credentials changed.";

        return null;
    }
}
