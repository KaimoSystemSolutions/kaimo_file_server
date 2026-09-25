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
    private readonly TimeSpan _refreshReuseGrace;

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
        // Deliberately short and separate from the web login lifetime (Jwt:ExpirationHours):
        // a leaked access token expires quickly, and clients renew it via the refresh token.
        _accessTokenSeconds = Math.Clamp(config.GetValue("Jwt:AccessTokenMinutes", 15), 1, 24 * 60) * 60;
        _refreshTokenDays = config.GetValue("Jwt:RefreshTokenDays", 30);
        // Grace window in which a replay of a just-rotated token is treated as a
        // benign concurrent-refresh race rather than theft (see RefreshAsync).
        // 0 disables the leniency and restores strict single-use behavior.
        _refreshReuseGrace =
            TimeSpan.FromSeconds(Math.Max(0, config.GetValue("Jwt:RefreshReuseGraceSeconds", 10)));
    }

    /// <summary>Mints an access token and a fresh refresh token for a device.</summary>
    public async Task<IssuedTokens> IssueAsync(UserContext user, Guid deviceId)
    {
        string accessToken = _jwt.GenerateToken(
            user.User.Id,
            user.User.Username,
            user.User.Name,
            user.Roles.Select(r => r.Name),
            deviceId,
            user.User.SecurityStamp,
            TimeSpan.FromSeconds(_accessTokenSeconds));

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

        // Replay of an already-rotated/revoked token. This is usually theft — but
        // it is also what a legitimate client produces when two refreshes race
        // (e.g. a mobile app's foreground and background-sync isolates each present
        // the same token before either has stored the rotated one). To avoid
        // punishing that benign race with a full device-chain revocation, a replay
        // of a *just-rotated* token whose replacement is still live is soft-rejected
        // within a short grace window: the client simply retries with the newer
        // token it already holds, and the chain survives. Anything else — a replay
        // after the window, or of a token revoked by logout/device-revocation (no
        // live replacement) — is treated as theft and revokes the whole chain.
        if (existing.RevokedAtUtc is not null)
        {
            var rotatedInto = existing.ReplacedByTokenId is { } replacementId
                ? await _refreshTokens.GetByIdAsync(replacementId)
                : null;

            if (IsBenignRefreshRace(
                    existing.RevokedAtUtc.Value,
                    existing.ReplacedByTokenId,
                    rotatedInto is not null && rotatedInto.IsActive(now),
                    now,
                    _refreshReuseGrace))
            {
                _logger.LogInformation(
                    "Refresh token replayed within the {GraceSeconds}s grace window for device " +
                    "{DeviceId}; treating as a concurrent-refresh race, chain left intact",
                    _refreshReuseGrace.TotalSeconds, existing.DeviceId);
                return new RefreshResult(RefreshOutcome.Invalid);
            }

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
        if (!await _refreshTokens.RotateAsync(existing, replacement))
        {
            // A concurrent refresh rotated this token first. Same handling as the
            // benign-race branch above: reject, the winner's chain stays intact.
            _logger.LogInformation(
                "Refresh token for device {DeviceId} was rotated concurrently; rejecting this attempt",
                existing.DeviceId);
            return new RefreshResult(RefreshOutcome.Invalid);
        }

        await _devices.TouchLastSeenAsync(device.Id, now);

        string accessToken = _jwt.GenerateToken(
            user.User.Id, user.User.Username, user.User.Name,
            user.Roles.Select(r => r.Name), device.Id,
            user.User.SecurityStamp, TimeSpan.FromSeconds(_accessTokenSeconds));

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

    /// <summary>
    /// Whether a replay of an already-revoked refresh token is a benign concurrent-
    /// refresh race rather than a replay to defend against. True only when the token
    /// was rotated (has a replacement), that replacement is still active, and the
    /// rotation happened no longer than <paramref name="grace"/> ago. A
    /// non-positive <paramref name="grace"/> disables the leniency entirely (strict
    /// single-use). Pure and side-effect-free so the decision is unit-testable.
    /// </summary>
    public static bool IsBenignRefreshRace(
        DateTime revokedAtUtc,
        Guid? replacedByTokenId,
        bool replacementActive,
        DateTime nowUtc,
        TimeSpan grace)
        => grace > TimeSpan.Zero
           && replacedByTokenId is not null
           && replacementActive
           && nowUtc - revokedAtUtc <= grace;

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
