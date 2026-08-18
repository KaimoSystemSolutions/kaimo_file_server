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

/// <summary>Runs Microsoft's OAuth device authorization grant using shared server-side state.</summary>
public interface IOneDriveDeviceAuthorizationService
{
    Task<OneDriveDeviceAuthorization> StartAsync(
        Guid shareId,
        string localPath,
        string authorizationTicket);

    Task<OneDriveDevicePollResult> PollAsync(string sessionId);
}

/// <summary>
/// Database-backed device authorization coordinator. Device codes and hand-off
/// context are protected at rest, browser session IDs are stored only as hashes,
/// and a short polling lease prevents duplicate token exchanges across instances.
/// </summary>
public sealed class OneDriveDeviceAuthorizationService : IOneDriveDeviceAuthorizationService
{
    private static readonly TimeSpan PollLeaseLifetime = TimeSpan.FromSeconds(45);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _timeProvider;
    private readonly MicrosoftIdentityConfiguration _identity;

    public OneDriveDeviceAuthorizationService(
        IHttpClientFactory httpClientFactory,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IDataProtectionProvider dataProtectionProvider,
        TimeProvider timeProvider,
        MicrosoftIdentityConfiguration? identity = null)
    {
        _httpClientFactory = httpClientFactory;
        _dbFactory = dbFactory;
        _protector = dataProtectionProvider.CreateProtector(
            "KaimoFiles.ExternalStorage.DeviceAuthorization", "v1");
        _timeProvider = timeProvider;
        _identity = identity ?? MicrosoftIdentityConfiguration.Create(
            OneDriveOAuthDefaults.ClientId, OneDriveOAuthDefaults.Authority);
    }

    /// <inheritdoc />
    public async Task<OneDriveDeviceAuthorization> StartAsync(
        Guid shareId,
        string localPath,
        string authorizationTicket)
    {
        var client = _httpClientFactory.CreateClient(nameof(OneDriveDeviceAuthorizationService));
        using var response = await client.PostAsync(
            _identity.DeviceCodeEndpoint,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _identity.PublicClientId,
                ["scope"] = OneDriveOAuthDefaults.Scope
            }));
        using var json = await ParseSuccessAsync(response);
        var root = json.RootElement;
        var deviceCode = RequiredString(root, "device_code");
        var userCode = RequiredString(root, "user_code");
        var verificationUri = RequiredString(root, "verification_uri");
        if (!Uri.TryCreate(verificationUri, UriKind.Absolute, out var verification)
            || verification.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Microsoft returned an invalid device verification URI.");

        var expiresIn = root.TryGetProperty("expires_in", out var expiryProperty)
            ? Math.Clamp(expiryProperty.GetInt32(), 60, 1800)
            : 900;
        var interval = root.TryGetProperty("interval", out var intervalProperty)
            ? Math.Clamp(intervalProperty.GetInt32(), 5, 60)
            : 5;
        var sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var payload = new DeviceSessionPayload(
            shareId, localPath, authorizationTicket, deviceCode);

        await using var db = await _dbFactory.CreateDbContextAsync();
        db.StorageDeviceAuthorizationSessions.Add(new StorageDeviceAuthorizationSession
        {
            SessionHash = Hash(sessionId),
            ProviderId = "onedrive",
            ProtectedPayload = _protector.Protect(JsonSerializer.Serialize(payload)),
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddSeconds(expiresIn),
            PollIntervalSeconds = interval,
            NextPollAtUtc = now
        });
        await db.SaveChangesAsync();
        _ = await db.StorageDeviceAuthorizationSessions
            .Where(session => session.ExpiresAtUtc <= now)
            .ExecuteDeleteAsync();

        return new OneDriveDeviceAuthorization(
            sessionId, userCode, verification.AbsoluteUri, expiresIn, interval);
    }

    /// <inheritdoc />
    public async Task<OneDriveDevicePollResult> PollAsync(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return OneDriveDevicePollResult.Failed(R("Web_CloudSync_Device_Expired"));

        var sessionHash = Hash(sessionId);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var pollLeaseId = Guid.NewGuid();
        await using var db = await _dbFactory.CreateDbContextAsync();
        var claimed = await db.StorageDeviceAuthorizationSessions
            .Where(session => session.SessionHash == sessionHash
                              && session.ExpiresAtUtc > now
                              && session.NextPollAtUtc <= now
                              && (session.PollLeaseUntilUtc == null || session.PollLeaseUntilUtc <= now))
            .ExecuteUpdateAsync(update => update
                .SetProperty(session => session.PollLeaseId, pollLeaseId)
                .SetProperty(session => session.PollLeaseUntilUtc, now.Add(PollLeaseLifetime)));

        var session = await db.StorageDeviceAuthorizationSessions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.SessionHash == sessionHash);
        if (session is null || session.ExpiresAtUtc <= now)
        {
            if (session is not null)
                _ = await db.StorageDeviceAuthorizationSessions
                    .Where(item => item.SessionHash == sessionHash)
                    .ExecuteDeleteAsync();
            return OneDriveDevicePollResult.Failed(R("Web_CloudSync_Device_Expired"));
        }

        if (claimed != 1)
        {
            var retryAfter = Math.Max(1, (int)Math.Ceiling(
                (session.NextPollAtUtc - now).TotalSeconds));
            return OneDriveDevicePollResult.Pending(retryAfter);
        }

        DeviceSessionPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<DeviceSessionPayload>(
                          _protector.Unprotect(session.ProtectedPayload))
                      ?? throw new JsonException();
        }
        catch (Exception exception) when (exception is JsonException or CryptographicException)
        {
            await DeleteSessionAsync(sessionHash);
            return OneDriveDevicePollResult.Failed(R("Web_CloudSync_Device_Failed"));
        }

        try
        {
            var client = _httpClientFactory.CreateClient(nameof(OneDriveDeviceAuthorizationService));
            using var response = await client.PostAsync(
                _identity.TokenEndpoint,
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["client_id"] = _identity.PublicClientId,
                    ["device_code"] = payload.DeviceCode
                }));
            var body = await response.Content.ReadAsStringAsync();
            JsonDocument? document = null;
            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                await DeleteSessionAsync(sessionHash);
                return OneDriveDevicePollResult.Failed(R("Web_CloudSync_Device_Failed"));
            }

            using (document)
            {
                if (response.IsSuccessStatusCode)
                {
                    var refreshToken = RequiredString(document.RootElement, "refresh_token");
                    var scope = OptionalString(document.RootElement, "scope") ?? OneDriveOAuthDefaults.Scope;
                    await DeleteSessionAsync(sessionHash);
                    return OneDriveDevicePollResult.Completed(
                        payload.ShareId, payload.LocalPath, payload.AuthorizationTicket, refreshToken, scope);
                }

                var providerError = ProviderErrorSanitizer.FromResponse(response.StatusCode, body);
                if (providerError.Code == "authorization_pending")
                {
                    await ReleasePollAsync(sessionHash, pollLeaseId, session.PollIntervalSeconds);
                    return OneDriveDevicePollResult.Pending(session.PollIntervalSeconds);
                }
                if (providerError.Code == "slow_down")
                {
                    var interval = Math.Min(session.PollIntervalSeconds + 5, 60);
                    await ReleasePollAsync(sessionHash, pollLeaseId, interval);
                    return OneDriveDevicePollResult.Pending(interval);
                }

                await DeleteSessionAsync(sessionHash);
                return OneDriveDevicePollResult.Failed(providerError.Code switch
                {
                    "expired_token" => R("Web_CloudSync_Device_Expired"),
                    _ => R("Web_CloudSync_Device_Failed")
                });
            }
        }
        catch
        {
            await ReleasePollAsync(sessionHash, pollLeaseId, session.PollIntervalSeconds);
            throw;
        }
    }

    private async Task ReleasePollAsync(string sessionHash, Guid pollLeaseId, int intervalSeconds)
    {
        var nextPoll = _timeProvider.GetUtcNow().UtcDateTime.AddSeconds(intervalSeconds);
        await using var db = await _dbFactory.CreateDbContextAsync();
        _ = await db.StorageDeviceAuthorizationSessions
            .Where(session => session.SessionHash == sessionHash && session.PollLeaseId == pollLeaseId)
            .ExecuteUpdateAsync(update => update
                .SetProperty(session => session.PollIntervalSeconds, intervalSeconds)
                .SetProperty(session => session.NextPollAtUtc, nextPoll)
                .SetProperty(session => session.PollLeaseId, (Guid?)null)
                .SetProperty(session => session.PollLeaseUntilUtc, (DateTime?)null));
    }

    private async Task DeleteSessionAsync(string sessionHash)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        _ = await db.StorageDeviceAuthorizationSessions
            .Where(session => session.SessionHash == sessionHash)
            .ExecuteDeleteAsync();
    }

    private static async Task<JsonDocument> ParseSuccessAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new ProviderRequestException(
                "microsoft_identity",
                ProviderErrorSanitizer.FromResponse(response.StatusCode, body));
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Microsoft returned an invalid authorization response.", exception);
        }
    }

    private static string RequiredString(JsonElement element, string propertyName)
        => OptionalString(element, propertyName)
           ?? throw new InvalidOperationException($"Microsoft did not return '{propertyName}'.");

    private static string? OptionalString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property)
           && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string R(string key) => Resources.ResourceManager.GetString(key) ?? key;

    private sealed record DeviceSessionPayload(
        Guid ShareId,
        string LocalPath,
        string AuthorizationTicket,
        string DeviceCode);
}

/// <summary>Browser-safe values required to display and poll a device authorization.</summary>
public sealed record OneDriveDeviceAuthorization(
    string SessionId,
    string UserCode,
    string VerificationUri,
    int ExpiresInSeconds,
    int PollIntervalSeconds);

/// <summary>Provider-neutral state returned by one device-code poll attempt.</summary>
public enum OneDriveDevicePollState
{
    Pending,
    Complete,
    Failed
}

/// <summary>Result of polling a device session; sensitive values remain server-side.</summary>
public sealed record OneDriveDevicePollResult(
    OneDriveDevicePollState State,
    int RetryAfterSeconds,
    Guid ShareId,
    string? LocalPath,
    string? AuthorizationTicket,
    string? RefreshToken,
    string? Scope,
    string? ErrorMessage)
{
    public static OneDriveDevicePollResult Pending(int retryAfterSeconds)
        => new(OneDriveDevicePollState.Pending, retryAfterSeconds, Guid.Empty, null, null, null, null, null);

    public static OneDriveDevicePollResult Completed(
        Guid shareId,
        string localPath,
        string authorizationTicket,
        string refreshToken,
        string scope)
        => new(OneDriveDevicePollState.Complete, 0, shareId, localPath, authorizationTicket, refreshToken, scope, null);

    public static OneDriveDevicePollResult Failed(string message)
        => new(OneDriveDevicePollState.Failed, 0, Guid.Empty, null, null, null, null, message);
}
