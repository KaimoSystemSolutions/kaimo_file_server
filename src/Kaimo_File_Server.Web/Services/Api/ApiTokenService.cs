using System.Security.Cryptography;
using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;

namespace Kaimo_File_Server.Web.Services.Api;

/// <summary>A freshly issued access + refresh token pair for a client device.</summary>
public sealed record IssuedTokens(
    string AccessToken,
    string RefreshToken,
    int ExpiresInSeconds);

/// <summary>Outcome of a refresh attempt.</summary>
public enum RefreshOutcome
{
    Success,
    /// <summary>Token unknown, malformed, expired, or its device/account is gone/disabled.</summary>
    Invalid,
    /// <summary>An already-rotated token was replayed — the whole device chain is revoked.</summary>
    Reuse,
}

/// <summary>Result of <see cref="ApiTokenService.RefreshAsync"/>.</summary>
public sealed record RefreshResult(RefreshOutcome Outcome, IssuedTokens? Tokens = null);

/// <summary>
/// Issues and rotates client-API tokens. Access tokens are short-lived JWTs
/// (minted by <see cref="JwtTokenService"/>); refresh tokens are opaque secrets
/// stored only as SHA-256 hashes and rotated on every use, with replay of a
/// rotated token treated as theft (the device's chain is revoked).
/// </summary>
public sealed class ApiTokenService
{
    private readonly JwtTokenService _jwt;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly ISyncDeviceRepository _devices;
    private readonly IUserContextFactory _userContextFactory;
    private readonly TimeProvider _clock;
    private readonly ILogger<ApiTokenService> _logger;
    private readonly int _accessTokenSeconds;
    private readonly int _refreshTokenDays;

    public ApiTokenService(
        JwtTokenService jwt,
        IRefreshTokenRepository refreshTokens,
        ISyncDeviceRepository devices,
        IUserContextFactory userContextFactory,
        TimeProvider clock,
        IConfiguration config,
        ILogger<ApiTokenService> logger)
    {
        _jwt = jwt;
        _refreshTokens = refreshTokens;
        _devices = devices;
        _userContextFactory = userContextFactory;
        _clock = clock;
        _logger = logger;
        _accessTokenSeconds = config.GetValue("Jwt:ExpirationHours", 24) * 3600;
        _refreshTokenDays = config.GetValue("Jwt:RefreshTokenDays", 30);
    }

    /// <summary>Mints an access token and a fresh refresh token for a device.</summary>
    public async Task<IssuedTokens> IssueAsync(UserContext user, Guid deviceId)
    {
        string accessToken = _jwt.GenerateToken(
            user.User.Id,
            user.User.Username,
            user.User.Name,
            user.Roles.Select(r => r.Name));

        (string secret, string hash) = GenerateOpaqueToken();
        var now = _clock.GetUtcNow().UtcDateTime;

        await _refreshTokens.CreateAsync(new RefreshToken
        {
            UserId = user.User.Id,
            DeviceId = deviceId,
            TokenHash = hash,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddDays(_refreshTokenDays),
        });

        return new IssuedTokens(accessToken, secret, _accessTokenSeconds);
    }

    /// <summary>
    /// Validates a presented refresh token, rotates it, and mints a new access
    /// token. Re-resolves the account each time so a disabled user cannot refresh.
    /// </summary>
    public async Task<RefreshResult> RefreshAsync(string presentedRefreshToken)
    {
        if (string.IsNullOrWhiteSpace(presentedRefreshToken))
            return new RefreshResult(RefreshOutcome.Invalid);

        string hash = Hash(presentedRefreshToken);
        var existing = await _refreshTokens.GetByHashAsync(hash);
        if (existing is null)
            return new RefreshResult(RefreshOutcome.Invalid);

        var now = _clock.GetUtcNow().UtcDateTime;

        // Replay of an already-rotated/revoked token: treat as theft and revoke
        // every active token for the device.
        if (existing.RevokedAtUtc is not null)
        {
            _logger.LogWarning(
                "Refresh token reuse detected for device {DeviceId}; revoking device token chain",
                existing.DeviceId);
            await _refreshTokens.RevokeAllForDeviceAsync(existing.DeviceId, now);
            return new RefreshResult(RefreshOutcome.Reuse);
        }

        if (!existing.IsActive(now))
            return new RefreshResult(RefreshOutcome.Invalid);

        var device = await _devices.GetByIdAsync(existing.DeviceId);
        if (device is null || !device.IsActive)
            return new RefreshResult(RefreshOutcome.Invalid);

        var user = await _userContextFactory.CreateByUserIdAsync(existing.UserId);
        if (user is null || !user.User.IsEnabled)
            return new RefreshResult(RefreshOutcome.Invalid);

        // Rotate: mint the replacement, then atomically revoke the old + insert new.
        (string secret, string newHash) = GenerateOpaqueToken();
        var replacement = new RefreshToken
        {
            UserId = user.User.Id,
            DeviceId = device.Id,
            TokenHash = newHash,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddDays(_refreshTokenDays),
        };
        existing.RevokedAtUtc = now;
        existing.ReplacedByTokenId = replacement.Id;
        await _refreshTokens.RotateAsync(existing, replacement);

        await _devices.TouchLastSeenAsync(device.Id, now);

        string accessToken = _jwt.GenerateToken(
            user.User.Id, user.User.Username, user.User.Name,
            user.Roles.Select(r => r.Name));

        return new RefreshResult(
            RefreshOutcome.Success,
            new IssuedTokens(accessToken, secret, _accessTokenSeconds));
    }

    /// <summary>Revokes a single presented refresh token (logout on this device).</summary>
    public async Task RevokeAsync(string presentedRefreshToken)
    {
        if (string.IsNullOrWhiteSpace(presentedRefreshToken))
            return;

        var existing = await _refreshTokens.GetByHashAsync(Hash(presentedRefreshToken));
        if (existing is { RevokedAtUtc: null })
        {
            existing.RevokedAtUtc = _clock.GetUtcNow().UtcDateTime;
            await _refreshTokens.UpdateAsync(existing);
        }
    }

    // ─────────────────────────── helpers ───────────────────────────

    private static (string secret, string hash) GenerateOpaqueToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        string secret = Base64UrlEncode(bytes);
        return (secret, Hash(secret));
    }

    private static string Hash(string secret)
    {
        byte[] digest = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexStringLower(digest);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
