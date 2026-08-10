using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Kaimo_File_Server.Infrastructure.Clouds;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Runs Microsoft's OAuth device authorization grant without a redirect URI or
/// client secret. Only the public application id is distributed in the image;
/// device codes and resulting tokens remain inside the individual server.
/// </summary>
public interface IOneDriveDeviceAuthorizationService
{
    /// <summary>
    /// Requests a Microsoft device code and stores the associated local sync
    /// context in a short-lived server-side session.
    /// </summary>
    Task<OneDriveDeviceAuthorization> StartAsync(
        Guid shareId,
        string localPath,
        string authorizationTicket);

    /// <summary>
    /// Polls Microsoft once for the specified session and returns a neutral
    /// pending, completed, or failed result to the controller.
    /// </summary>
    Task<OneDriveDevicePollResult> PollAsync(string sessionId);
}

/// <summary>
/// In-memory coordinator for Microsoft's public-client device-code flow. It
/// serializes polling per session and follows Microsoft's retry intervals.
/// </summary>
public sealed class OneDriveDeviceAuthorizationService : IOneDriveDeviceAuthorizationService
{
    private readonly ConcurrentDictionary<string, DeviceSession> _sessions = new(StringComparer.Ordinal);
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>Creates the coordinator with application configuration and managed HTTP clients.</summary>
    public OneDriveDeviceAuthorizationService(
        IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc />
    public async Task<OneDriveDeviceAuthorization> StartAsync(
        Guid shareId,
        string localPath,
        string authorizationTicket)
    {
        RemoveExpiredSessions();
        var client = _httpClientFactory.CreateClient(nameof(OneDriveDeviceAuthorizationService));
        using var response = await client.PostAsync(
            OneDriveOAuthDefaults.DeviceCodeEndpoint,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = OneDriveOAuthDefaults.ClientId,
                ["scope"] = OneDriveOAuthDefaults.Scope
            }));
        using var json = await ParseSuccessAsync(response);
        var root = json.RootElement;
        var deviceCode = RequiredString(root, "device_code");
        var userCode = RequiredString(root, "user_code");
        var verificationUri = RequiredString(root, "verification_uri");
        if (!Uri.TryCreate(verificationUri, UriKind.Absolute, out var verification)
            || verification.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Microsoft returned an invalid device verification URI.");
        }

        var expiresIn = root.TryGetProperty("expires_in", out var expiryProperty)
            ? Math.Clamp(expiryProperty.GetInt32(), 60, 1800)
            : 900;
        var interval = root.TryGetProperty("interval", out var intervalProperty)
            ? Math.Clamp(intervalProperty.GetInt32(), 5, 60)
            : 5;
        var sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var session = new DeviceSession(
            shareId,
            localPath,
            authorizationTicket,
            deviceCode,
            DateTimeOffset.UtcNow.AddSeconds(expiresIn),
            interval);
        _sessions[sessionId] = session;

        return new OneDriveDeviceAuthorization(
            sessionId,
            userCode,
            verification.AbsoluteUri,
            expiresIn,
            interval);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Concurrent or early polls are answered locally instead of producing
    /// duplicate token requests. Microsoft's slow_down response increases the
    /// stored interval for every following poll.
    /// </remarks>
    public async Task<OneDriveDevicePollResult> PollAsync(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return OneDriveDevicePollResult.Failed("The Microsoft authorization session has expired.");

        var now = DateTimeOffset.UtcNow;
        int retryAfter;
        lock (session.SyncRoot)
        {
            if (session.ExpiresAt <= now)
            {
                _sessions.TryRemove(sessionId, out _);
                return OneDriveDevicePollResult.Failed("The Microsoft authorization code has expired.");
            }

            if (session.IsPolling || session.NextPollAt > now)
            {
                retryAfter = Math.Max(
                    1,
                    (int)Math.Ceiling((session.NextPollAt - now).TotalSeconds));
                return OneDriveDevicePollResult.Pending(retryAfter);
            }

            session.IsPolling = true;
            session.NextPollAt = now.AddSeconds(session.IntervalSeconds);
            retryAfter = session.IntervalSeconds;
        }

        try
        {
            var client = _httpClientFactory.CreateClient(nameof(OneDriveDeviceAuthorizationService));
            using var response = await client.PostAsync(
                OneDriveOAuthDefaults.TokenEndpoint,
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["client_id"] = OneDriveOAuthDefaults.ClientId,
                    ["device_code"] = session.DeviceCode
                }));
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

            if (response.IsSuccessStatusCode)
            {
                var refreshToken = RequiredString(json.RootElement, "refresh_token");
                var scope = OptionalString(json.RootElement, "scope") ?? OneDriveOAuthDefaults.Scope;
                _sessions.TryRemove(sessionId, out _);
                return OneDriveDevicePollResult.Completed(
                    session.ShareId,
                    session.LocalPath,
                    session.AuthorizationTicket,
                    refreshToken,
                    scope);
            }

            var providerError = OptionalString(json.RootElement, "error");
            if (string.Equals(providerError, "authorization_pending", StringComparison.Ordinal))
                return OneDriveDevicePollResult.Pending(retryAfter);

            if (string.Equals(providerError, "slow_down", StringComparison.Ordinal))
            {
                lock (session.SyncRoot)
                {
                    session.IntervalSeconds = Math.Min(session.IntervalSeconds + 5, 60);
                    session.NextPollAt = DateTimeOffset.UtcNow.AddSeconds(session.IntervalSeconds);
                    retryAfter = session.IntervalSeconds;
                }
                return OneDriveDevicePollResult.Pending(retryAfter);
            }

            _sessions.TryRemove(sessionId, out _);
            var description = OptionalString(json.RootElement, "error_description");
            return OneDriveDevicePollResult.Failed(providerError switch
            {
                "authorization_declined" => "Microsoft authorization was declined.",
                "expired_token" => "The Microsoft authorization code has expired.",
                _ => description ?? "Microsoft authorization failed."
            });
        }
        catch (JsonException)
        {
            _sessions.TryRemove(sessionId, out _);
            return OneDriveDevicePollResult.Failed("Microsoft returned an invalid authorization response.");
        }
        finally
        {
            lock (session.SyncRoot)
                session.IsPolling = false;
        }
    }

    /// <summary>Opportunistically removes expired device sessions before issuing a new one.</summary>
    private void RemoveExpiredSessions()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (id, session) in _sessions)
        {
            if (session.ExpiresAt <= now)
                _sessions.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Parses a successful identity-platform response or raises a diagnostic
    /// HTTP exception containing the provider response body.
    /// </summary>
    private static async Task<JsonDocument> ParseSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        var body = await response.Content.ReadAsStringAsync();
        throw new HttpRequestException(
            $"Microsoft identity platform returned {(int)response.StatusCode} ({response.ReasonPhrase}): {body}",
            null,
            response.StatusCode);
    }

    /// <summary>Reads a mandatory Microsoft response string with a precise error.</summary>
    private static string RequiredString(JsonElement element, string propertyName)
        => OptionalString(element, propertyName)
           ?? throw new InvalidOperationException($"Microsoft did not return '{propertyName}'.");

    /// <summary>Reads an optional JSON string without throwing for missing or null fields.</summary>
    private static string? OptionalString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property)
           && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private sealed class DeviceSession(
        Guid shareId,
        string localPath,
        string authorizationTicket,
        string deviceCode,
        DateTimeOffset expiresAt,
        int intervalSeconds)
    {
        public object SyncRoot { get; } = new();
        public Guid ShareId { get; } = shareId;
        public string LocalPath { get; } = localPath;
        public string AuthorizationTicket { get; } = authorizationTicket;
        public string DeviceCode { get; } = deviceCode;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public int IntervalSeconds { get; set; } = intervalSeconds;
        public DateTimeOffset NextPollAt { get; set; }
        public bool IsPolling { get; set; }
    }
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

/// <summary>
/// Result of polling a device session. Sensitive tokens are populated only for
/// a completed server-side exchange and are never rendered into the HTML page.
/// </summary>
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
    /// <summary>Creates a result instructing the browser when to poll again.</summary>
    public static OneDriveDevicePollResult Pending(int retryAfterSeconds)
        => new(OneDriveDevicePollState.Pending, retryAfterSeconds, Guid.Empty, null, null, null, null, null);

    /// <summary>Creates the completed hand-off containing the persisted sync context.</summary>
    public static OneDriveDevicePollResult Completed(
        Guid shareId,
        string localPath,
        string authorizationTicket,
        string refreshToken,
        string scope)
        => new(
            OneDriveDevicePollState.Complete,
            0,
            shareId,
            localPath,
            authorizationTicket,
            refreshToken,
            scope,
            null);

    /// <summary>Creates a terminal failure safe to report to the authorization page.</summary>
    public static OneDriveDevicePollResult Failed(string message)
        => new(OneDriveDevicePollState.Failed, 0, Guid.Empty, null, null, null, null, message);
}
