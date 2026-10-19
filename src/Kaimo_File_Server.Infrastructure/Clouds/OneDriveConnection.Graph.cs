using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Kaimo_File_Server.Core.Services;

namespace Kaimo_File_Server.Infrastructure.Clouds;

public partial class OneDriveConnection
{
    /// <summary>Resolves the current user's drive root id for folder creation.</summary>
    private async Task<string> GetRootIdAsync(CancellationToken cancellationToken)
    {
        using var response = await SendGraphAsync(
            HttpMethod.Get,
            "/me/drive/root?$select=id",
            cancellationToken: cancellationToken);
        using var json = await ParseSuccessAsync(response, cancellationToken);
        return json.RootElement.GetProperty("id").GetString()
               ?? throw new InvalidOperationException("Microsoft Graph did not return the root folder id.");
    }

    /// <summary>
    /// Looks up a drive item without treating Graph's expected 404 response as
    /// an exception, allowing directory creation to continue.
    /// </summary>
    private async Task<DriveItemReference?> GetItemByPathAsync(
        string path,
        CancellationToken cancellationToken)
    {
        using var response = await SendGraphAsync(
            HttpMethod.Get,
            $"/me/drive/root:/{EncodePath(path)}?$select=id,folder",
            cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        using var json = await ParseSuccessAsync(response, cancellationToken);
        return new DriveItemReference(
            json.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Microsoft Graph did not return an item id."),
            json.RootElement.TryGetProperty("folder", out _));
    }

    /// <summary>
    /// Sends an authenticated Graph request after validating that absolute
    /// continuation links remain on Microsoft's HTTPS Graph origin.
    /// </summary>
    private async Task<HttpResponseMessage> SendGraphAsync(
        HttpMethod method,
        string url,
        HttpContent? content = null,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead,
        CancellationToken cancellationToken = default)
    {
        var token = await GetAccessTokenAsync(cancellationToken);
        // A leading slash can be interpreted as an absolute file:// URI on
        // Linux. Graph API paths are intentionally resolved against our fixed
        // HTTPS origin before accepting absolute @odata.nextLink values.
        Uri requestUrl;
        if (url.StartsWith("/", StringComparison.Ordinal))
        {
            requestUrl = new Uri(GraphBaseUrl + url, UriKind.Absolute);
        }
        else if (!Uri.TryCreate(url, UriKind.Absolute, out requestUrl!))
        {
            throw new InvalidOperationException("Microsoft Graph returned an invalid continuation URL.");
        }

        if (requestUrl.Scheme != Uri.UriSchemeHttps
            || !string.Equals(requestUrl.Host, "graph.microsoft.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Microsoft Graph returned an untrusted continuation URL.");
        }

        using var request = new HttpRequestMessage(method, requestUrl) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _httpClient.SendAsync(request, completion, cancellationToken);
    }

    /// <summary>
    /// Returns a cached access token or serializes refresh-token exchange. Any
    /// refresh-token rotation is retained for later persistence by the caller.
    /// </summary>
    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_accessToken)
            && _accessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
            return _accessToken;

        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrEmpty(_accessToken)
                && _accessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
                return _accessToken;

            await using var refreshLease = _acquireRefreshLease is null
                ? null
                : await _acquireRefreshLease(cancellationToken)
                  ?? throw new InvalidOperationException(
                      "The storage connection credential refresh is already in progress.");

            // A provider may have returned a rotated refresh token immediately
            // before a database write failed. Retry that write before reloading
            // the database value or contacting the provider again.
            if (_hasPendingCredentialChanges && _persistRotatedCredentials is not null)
            {
                await _persistRotatedCredentials(_data, cancellationToken);
                _hasPendingCredentialChanges = false;
            }

            if (_reloadCredentials is not null)
            {
                var latest = await _reloadCredentials(cancellationToken);
                if (!latest.TryGetValue("refreshToken", out var latestRefreshToken)
                    || string.IsNullOrWhiteSpace(latestRefreshToken))
                    throw new InvalidOperationException("The Microsoft OneDrive refresh token is missing.");
                _data["refreshToken"] = latestRefreshToken;
                if (latest.TryGetValue("scope", out var latestScope) && !string.IsNullOrWhiteSpace(latestScope))
                    _data["scope"] = latestScope;
            }

            using var tokenTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            tokenTimeout.CancelAfter(TokenExchangeTimeout);
            using var response = await _httpClient.PostAsync(
                _identity.TokenEndpoint,
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = _identity.PublicClientId,
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = _data["refreshToken"],
                    ["scope"] = _data["scope"]
                }),
                tokenTimeout.Token);
            using var json = await ParseSuccessAsync(response, tokenTimeout.Token);
            var accessToken = json.RootElement.GetProperty("access_token").GetString()
                              ?? throw new InvalidOperationException("Microsoft did not return an access token.");
            var expiresIn = json.RootElement.TryGetProperty("expires_in", out var expiry)
                ? expiry.GetInt32()
                : 3600;
            var accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);

            var rotatedRefreshToken = GetOptionalString(json.RootElement, "refresh_token");
            if (!string.IsNullOrWhiteSpace(rotatedRefreshToken)
                && !string.Equals(rotatedRefreshToken, _data["refreshToken"], StringComparison.Ordinal))
            {
                _data["refreshToken"] = rotatedRefreshToken;
                _hasPendingCredentialChanges = true;
                if (_persistRotatedCredentials is not null)
                {
                    await _persistRotatedCredentials(_data, cancellationToken);
                    _hasPendingCredentialChanges = false;
                }
            }

            // The access token becomes visible to callers only after a rotated
            // refresh token has been durably saved.
            _accessToken = accessToken;
            _accessTokenExpiresAt = accessTokenExpiresAt;
            return accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <summary>Validates a Graph response and parses its JSON body cancellably.</summary>
    private static async Task<JsonDocument> ParseSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken = default)
    {
        await EnsureSuccessAsync(response, cancellationToken);
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(
            responseStream,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Converts unsuccessful Graph responses into a sanitized exception. Raw
    /// provider bodies are deliberately discarded at this boundary.
    /// </summary>
    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken = default)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new ProviderRequestException(
            "microsoft_graph",
            ProviderErrorSanitizer.FromResponse(response.StatusCode, body));
    }

    /// <summary>Normalizes provider paths to slash-separated, root-relative form.</summary>
    private static string NormalizePath(string? path)
        => string.IsNullOrWhiteSpace(path) ? "" : path.Replace('\\', '/').Trim('/');

    /// <summary>Normalizes and validates that a non-root file path was supplied.</summary>
    private static string RequireFilePath(string path)
    {
        var normalized = NormalizePath(path);
        return normalized.Length == 0
            ? throw new ArgumentException("A OneDrive file path is required.", nameof(path))
            : normalized;
    }

    /// <summary>Escapes each path segment without encoding directory separators.</summary>
    private static string EncodePath(string path)
        => string.Join('/', NormalizePath(path)
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));

    /// <summary>Combines provider paths while preserving one leading root slash.</summary>
    private static string CombinePath(string parent, string child)
        => NormalizePath(parent) is { Length: > 0 } normalizedParent
            ? $"/{normalizedParent}/{child}"
            : $"/{child}";

    private static string ShareRelativePathForGraph(string parent, string child)
        => NormalizePath(parent) is { Length: > 0 } normalizedParent
            ? $"{normalizedParent}/{child}"
            : child;

    /// <summary>Reads an optional JSON string without accepting non-string values.</summary>
    private static string? GetOptionalString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;

    private static DateTime ParseGraphDate(JsonElement element, string propertyName)
        => DateTimeOffset.TryParse(
            GetOptionalString(element, propertyName),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var value)
            ? value.UtcDateTime
            : DateTime.MinValue;
}
