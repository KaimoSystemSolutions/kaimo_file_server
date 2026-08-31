using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Clouds;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Runs Dropbox's OAuth 2.0 authorization-code grant with PKCE and no redirect
/// URI. The user copies the authorization code Dropbox displays and pastes it
/// back into Kaimo. Only a public application key is required; no secret and no
/// inbound callback endpoint are involved. The PKCE verifier and hand-off context
/// stay server-side, protected at rest, and the browser sees only an opaque
/// session id stored as a SHA-256 hash.
/// </summary>
public interface IDropboxAuthorizationService
{
    /// <summary>True when a Dropbox application key is configured for authorization.</summary>
    bool IsConfigured { get; }

    Task<DropboxAuthorizationStart> StartAsync(Guid connectionId, string authorizationTicket);

    Task<DropboxAuthorizationResult> CompleteAsync(string sessionId, string code);
}

/// <inheritdoc cref="IDropboxAuthorizationService" />
public sealed class DropboxAuthorizationService : IDropboxAuthorizationService
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(15);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _timeProvider;
    private readonly DropboxIdentityConfiguration _identity;

    public DropboxAuthorizationService(
        IHttpClientFactory httpClientFactory,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IDataProtectionProvider dataProtectionProvider,
        TimeProvider timeProvider,
        DropboxIdentityConfiguration identity)
    {
        _httpClientFactory = httpClientFactory;
        _dbFactory = dbFactory;
        _protector = dataProtectionProvider.CreateProtector(
            "KaimoFiles.ExternalStorage.DropboxAuthorization", "v1");
        _timeProvider = timeProvider;
        _identity = identity;
    }

    public bool IsConfigured => _identity.IsConfigured;

    /// <inheritdoc />
    public async Task<DropboxAuthorizationStart> StartAsync(Guid connectionId, string authorizationTicket)
    {
        if (!_identity.IsConfigured)
            throw new InvalidOperationException("No Dropbox application key is configured.");

        var codeVerifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var codeChallenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
        var sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var payload = new SessionPayload(connectionId, authorizationTicket, codeVerifier);

        await using var db = await _dbFactory.CreateDbContextAsync();
        db.StorageDeviceAuthorizationSessions.Add(new StorageDeviceAuthorizationSession
        {
            SessionHash = Hash(sessionId),
            ProviderId = "dropbox",
            ProtectedPayload = _protector.Protect(JsonSerializer.Serialize(payload)),
            CreatedAtUtc = now,
            ExpiresAtUtc = now.Add(SessionLifetime),
            PollIntervalSeconds = 0,
            NextPollAtUtc = now
        });
        await db.SaveChangesAsync();
        _ = await db.StorageDeviceAuthorizationSessions
            .Where(session => session.ExpiresAtUtc <= now)
            .ExecuteDeleteAsync();

        var authorizeUrl = DropboxOAuthDefaults.AuthorizeEndpoint
            + "?response_type=code"
            + "&token_access_type=offline"
            + "&code_challenge_method=S256"
            + $"&client_id={Uri.EscapeDataString(_identity.AppKey)}"
            + $"&code_challenge={Uri.EscapeDataString(codeChallenge)}"
            + $"&scope={Uri.EscapeDataString(DropboxOAuthDefaults.Scope)}";

        return new DropboxAuthorizationStart(sessionId, authorizeUrl);
    }

    /// <inheritdoc />
    public async Task<DropboxAuthorizationResult> CompleteAsync(string sessionId, string code)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(code))
            return DropboxAuthorizationResult.Failed(R("Web_CloudAccess_Dropbox_Expired"));

        var sessionHash = Hash(sessionId);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        await using var db = await _dbFactory.CreateDbContextAsync();
        var session = await db.StorageDeviceAuthorizationSessions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.SessionHash == sessionHash && item.ProviderId == "dropbox");
        if (session is null || session.ExpiresAtUtc <= now)
        {
            if (session is not null)
                await DeleteSessionAsync(sessionHash);
            return DropboxAuthorizationResult.Failed(R("Web_CloudAccess_Dropbox_Expired"));
        }

        SessionPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<SessionPayload>(
                          _protector.Unprotect(session.ProtectedPayload))
                      ?? throw new JsonException();
        }
        catch (Exception exception) when (exception is JsonException or CryptographicException)
        {
            await DeleteSessionAsync(sessionHash);
            return DropboxAuthorizationResult.Failed(R("Web_CloudAccess_Dropbox_Failed"));
        }

        try
        {
            var client = _httpClientFactory.CreateClient(nameof(DropboxAuthorizationService));
            using var response = await client.PostAsync(
                DropboxOAuthDefaults.TokenEndpoint,
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code.Trim(),
                    ["client_id"] = _identity.AppKey,
                    ["code_verifier"] = payload.CodeVerifier
                }));
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                await DeleteSessionAsync(sessionHash);
                return DropboxAuthorizationResult.Failed(R("Web_CloudAccess_Dropbox_Failed"));
            }

            using var json = JsonDocument.Parse(body);
            var refreshToken = json.RootElement.TryGetProperty("refresh_token", out var refresh)
                ? refresh.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                await DeleteSessionAsync(sessionHash);
                return DropboxAuthorizationResult.Failed(R("Web_CloudAccess_Dropbox_Failed"));
            }

            var scope = json.RootElement.TryGetProperty("scope", out var scopeElement)
                && scopeElement.ValueKind == JsonValueKind.String
                    ? scopeElement.GetString()!
                    : DropboxOAuthDefaults.Scope;
            await DeleteSessionAsync(sessionHash);
            return DropboxAuthorizationResult.Completed(
                payload.ConnectionId, payload.AuthorizationTicket, refreshToken!, scope);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            await DeleteSessionAsync(sessionHash);
            return DropboxAuthorizationResult.Failed(R("Web_CloudAccess_Dropbox_Failed"));
        }
    }

    private async Task DeleteSessionAsync(string sessionHash)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        _ = await db.StorageDeviceAuthorizationSessions
            .Where(session => session.SessionHash == sessionHash)
            .ExecuteDeleteAsync();
    }

    private static string Base64Url(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string R(string key) => Resources.ResourceManager.GetString(key) ?? key;

    private sealed record SessionPayload(Guid ConnectionId, string AuthorizationTicket, string CodeVerifier);
}

/// <summary>Browser-safe values required to display and complete a Dropbox authorization.</summary>
public sealed record DropboxAuthorizationStart(string SessionId, string AuthorizeUrl);

/// <summary>State returned by a Dropbox authorization completion attempt.</summary>
public enum DropboxAuthorizationState
{
    Complete,
    Failed
}

/// <summary>Result of exchanging a pasted Dropbox code; sensitive values remain server-side.</summary>
public sealed record DropboxAuthorizationResult(
    DropboxAuthorizationState State,
    Guid ConnectionId,
    string? AuthorizationTicket,
    string? RefreshToken,
    string? Scope,
    string? ErrorMessage)
{
    public static DropboxAuthorizationResult Completed(
        Guid connectionId, string authorizationTicket, string refreshToken, string scope)
        => new(DropboxAuthorizationState.Complete, connectionId, authorizationTicket, refreshToken, scope, null);

    public static DropboxAuthorizationResult Failed(string message)
        => new(DropboxAuthorizationState.Failed, Guid.Empty, null, null, null, message);
}
