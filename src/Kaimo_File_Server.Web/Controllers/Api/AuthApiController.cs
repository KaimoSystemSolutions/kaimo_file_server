using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Web.Services.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Kaimo_File_Server.Web.Controllers.Api;

// ─────────────────────────── request/response DTOs ───────────────────────────

/// <summary>Login credentials plus the device this session belongs to.</summary>
/// <param name="Username">Login name.</param>
/// <param name="Password">Password.</param>
/// <param name="DeviceId">
/// The caller's existing device id, if it already registered one; omit or send
/// null on first login to have a new device created.
/// </param>
/// <param name="DeviceName">Display name for a newly created device.</param>
/// <param name="Platform">Advisory platform hint (e.g. "android", "windows").</param>
public sealed record LoginRequest(
    string Username,
    string Password,
    Guid? DeviceId = null,
    string? DeviceName = null,
    string? Platform = null);

/// <summary>Tokens plus the device id the caller should persist and reuse.</summary>
public sealed record TokenResponse(
    string AccessToken,
    string RefreshToken,
    int ExpiresInSeconds,
    Guid DeviceId);

/// <summary>A presented refresh token.</summary>
public sealed record RefreshRequest(string RefreshToken);

/// <summary>
/// Authentication and device registration for the client API. Login delegates to
/// the shared <see cref="ILoginService"/>, so brute-force lockout and
/// username-enumeration resistance are identical to the web UI.
/// </summary>
[AllowAnonymous]
[Route("api/v1/auth")]
public sealed class AuthApiController : ApiControllerBase
{
    private readonly ILoginService _loginService;
    private readonly IUserContextFactory _userContextFactory;
    private readonly ISyncDeviceRepository _devices;
    private readonly ApiTokenService _tokens;
    private readonly TimeProvider _clock;
    private readonly ILogger<AuthApiController> _logger;

    public AuthApiController(
        ILoginService loginService,
        IUserContextFactory userContextFactory,
        ISyncDeviceRepository devices,
        ApiTokenService tokens,
        TimeProvider clock,
        ILogger<AuthApiController> logger)
    {
        _loginService = loginService;
        _userContextFactory = userContextFactory;
        _devices = devices;
        _tokens = tokens;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Authenticates a user and issues an access + refresh token pair.</summary>
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        NoStore();

        if (request is null || string.IsNullOrWhiteSpace(request.Username) ||
            string.IsNullOrWhiteSpace(request.Password))
            return ApiBadRequest("credentials_required", "Username and password are required.");

        var result = await _loginService.AuthenticateAsync(request.Username, request.Password);
        switch (result.Outcome)
        {
            case LoginOutcome.Success:
                var user = result.UserContext!;
                var device = await ResolveOrCreateDeviceAsync(user.User.Id, request);
                var issued = await _tokens.IssueAsync(user, device.Id);
                return Ok(ToResponse(issued, device.Id));

            case LoginOutcome.LockedOut:
                Response.Headers[HeaderNames.RetryAfter] =
                    ((int)Math.Ceiling(result.RetryAfter.TotalSeconds)).ToString();
                return StatusCode(StatusCodes.Status429TooManyRequests,
                    new ApiError("locked_out", "Too many attempts. Try again later."));

            case LoginOutcome.AccountDisabled:
                // Log the real reason internally, but never disclose it outward:
                // return the same 401 as invalid credentials so a caller cannot tell
                // a disabled (but otherwise valid) account from a wrong password.
                _logger.LogWarning("Login rejected – account disabled: {Username}", request.Username);
                return ApiUnauthorized("Invalid username or password.");

            default:
                return ApiUnauthorized("Invalid username or password.");
        }
    }

    /// <summary>Rotates a refresh token and returns a new access token.</summary>
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest request)
    {
        NoStore();

        var result = await _tokens.RefreshAsync(request?.RefreshToken ?? "");
        return result.Outcome switch
        {
            RefreshOutcome.Success => Ok(new TokenResponse(
                result.Tokens!.AccessToken,
                result.Tokens.RefreshToken,
                result.Tokens.ExpiresInSeconds,
                DeviceId: Guid.Empty)),
            RefreshOutcome.Reuse => ApiUnauthorized("Refresh token was already used; sign in again."),
            _ => ApiUnauthorized("Refresh token is invalid or expired."),
        };
    }

    /// <summary>Revokes the presented refresh token (sign out on this device).</summary>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromBody] RefreshRequest request)
    {
        NoStore();
        await _tokens.RevokeAsync(request?.RefreshToken ?? "");
        return NoContent();
    }

    // ─────────────────────────── helpers ───────────────────────────

    private async Task<SyncDevice> ResolveOrCreateDeviceAsync(Guid userId, LoginRequest request)
    {
        var now = _clock.GetUtcNow().UtcDateTime;

        if (request.DeviceId is { } id)
        {
            var existing = await _devices.GetByIdAsync(id);
            // Only reuse a device the same user owns and that is still active.
            if (existing is { IsActive: true } && existing.UserId == userId)
            {
                existing.LastSeenUtc = now;
                if (!string.IsNullOrWhiteSpace(request.DeviceName))
                    existing.DisplayName = request.DeviceName!;
                if (!string.IsNullOrWhiteSpace(request.Platform))
                    existing.Platform = request.Platform!;
                await _devices.UpdateAsync(existing);
                return existing;
            }
        }

        return await _devices.CreateAsync(new SyncDevice
        {
            UserId = userId,
            DisplayName = string.IsNullOrWhiteSpace(request.DeviceName)
                ? "New device"
                : request.DeviceName!,
            Platform = request.Platform ?? string.Empty,
            CreatedAtUtc = now,
            LastSeenUtc = now,
        });
    }

    private static TokenResponse ToResponse(IssuedTokens issued, Guid deviceId)
        => new(issued.AccessToken, issued.RefreshToken, issued.ExpiresInSeconds, deviceId);

    private void NoStore()
    {
        Response.Headers[HeaderNames.CacheControl] = "no-store";
        Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
    }
}
