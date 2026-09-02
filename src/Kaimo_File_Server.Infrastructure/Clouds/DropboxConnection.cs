using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.DataServices;
using Microsoft.Extensions.Configuration;

namespace Kaimo_File_Server.Infrastructure.Clouds;

/// <summary>
/// Provider-neutral Dropbox connection backed by the Dropbox HTTP API v2. No
/// Dropbox SDK type crosses the cloud abstraction, so the UI and sync engine
/// remain reusable. Dropbox refresh tokens are long-lived and are not rotated on
/// refresh, so this connection has no rotated-credential persistence path.
/// </summary>
public sealed class DropboxConnection : ICloudConnection, IAsyncDisposable
{
    // Single-shot upload limit is 150 MB; larger files use an upload session.
    private const long UploadSessionThreshold = 140L * 1024 * 1024;
    private const int UploadChunkSize = 16 * 1024 * 1024;
    private const string ApiBase = "https://api.dropboxapi.com";
    private const string ContentBase = "https://content.dropboxapi.com";
    private static readonly TimeSpan TokenExchangeTimeout = TimeSpan.FromSeconds(60);

    private readonly Dictionary<string, string> _data;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly DropboxIdentityConfiguration _identity;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt;

    /// <summary>
    /// Creates a Dropbox connection from the mapping's persisted refresh token.
    /// The optional HTTP client keeps provider behavior directly testable.
    /// </summary>
    public DropboxConnection(
        Dictionary<string, string> data,
        HttpClient? httpClient = null,
        DropboxIdentityConfiguration? identity = null)
    {
        _data = data;
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _identity = identity ?? DropboxIdentityConfiguration.Create(DropboxOAuthDefaults.DefaultAppKey);

        if (!_data.TryGetValue("refreshToken", out var refreshToken)
            || string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidOperationException("The Dropbox refresh token is missing.");
        }
    }

    public string ServiceName => "Dropbox";

    /// <inheritdoc />
    public async Task<CloudAccountInfo?> GetAccountInfoAsync()
    {
        // get_current_account takes a null argument, sent as a JSON null body.
        using var response = await SendRpcAsync("/2/users/get_current_account", null);
        using var json = await ParseSuccessAsync(response);
        var root = json.RootElement;
        var displayName = root.TryGetProperty("name", out var name)
            ? GetOptionalString(name, "display_name")
            : null;
        return new CloudAccountInfo(displayName, GetOptionalString(root, "email"), null);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CloudItemMeta>> ListAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var result = new List<CloudItemMeta>();
        var body = JsonSerializer.Serialize(new
        {
            path = DropboxPath(path),
            recursive = false,
            include_deleted = false,
            include_mounted_folders = true,
            limit = 2000
        });
        var url = "/2/files/list_folder";

        while (true)
        {
            using var response = await SendRpcAsync(url, body, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Conflict)
                return []; // path/not_found for a missing directory.
            using var json = await ParseSuccessAsync(response, cancellationToken);
            var root = json.RootElement;
            foreach (var entry in root.GetProperty("entries").EnumerateArray())
            {
                var tag = GetOptionalString(entry, ".tag");
                if (tag is not ("file" or "folder"))
                    continue; // Skip deleted tombstones and unknown entry kinds.
                var isDirectory = tag == "folder";
                var name = GetOptionalString(entry, "name") ?? string.Empty;
                var itemPath = GetOptionalString(entry, "path_display")
                               ?? CombinePath(path, name);
                var size = !isDirectory && entry.TryGetProperty("size", out var sizeProperty)
                    ? sizeProperty.GetInt64()
                    : 0L;
                result.Add(new CloudItemMeta(
                    isDirectory,
                    name,
                    itemPath,
                    size,
                    ParseDropboxDate(entry, "server_modified")));
            }

            if (!root.TryGetProperty("has_more", out var hasMore) || !hasMore.GetBoolean())
                break;
            var cursor = root.GetProperty("cursor").GetString();
            body = JsonSerializer.Serialize(new { cursor });
            url = "/2/files/list_folder/continue";
        }

        return result;
    }

    /// <inheritdoc />
    public async Task DownloadAsync(
        string path,
        Stream target,
        CancellationToken cancellationToken = default)
    {
        var arg = JsonSerializer.Serialize(new { path = DropboxPath(RequireFilePath(path)) });
        using var request = new HttpRequestMessage(HttpMethod.Post, ContentBase + "/2/files/download");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetAccessTokenAsync(cancellationToken));
        request.Headers.Add("Dropbox-API-Arg", arg);
        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
            throw new FileNotFoundException("The Dropbox file was not found.", path);
        await EnsureSuccessAsync(response, cancellationToken);
        await response.Content.CopyToAsync(target, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>Dropbox creates missing parents on upload, so only explicit
    /// directory creation walks the path. Each segment ignores an existing folder.</remarks>
    public async Task CreateDirectoryAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizePath(path);
        if (normalized.Length == 0)
            return;

        var current = string.Empty;
        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            current = current.Length == 0 ? segment : $"{current}/{segment}";
            var body = JsonSerializer.Serialize(new { path = "/" + current, autorename = false });
            using var response = await SendRpcAsync("/2/files/create_folder_v2", body, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Conflict)
                continue; // The folder already exists; treat creation as idempotent.
            await EnsureSuccessAsync(response, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task UploadAsync(
        string path,
        Stream data,
        DateTime modifiedTime,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = DropboxPath(RequireFilePath(path));
        var clientModified = modifiedTime.ToUniversalTime().ToString(
            "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        if (data.CanSeek && data.Length - data.Position > UploadSessionThreshold)
            await UploadLargeFileAsync(normalizedPath, clientModified, data, cancellationToken);
        else
            await UploadSmallFileAsync(normalizedPath, clientModified, data, cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(
        string path,
        bool isDirectory,
        CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.Serialize(new { path = DropboxPath(RequireFilePath(path)) });
        using var response = await SendRpcAsync("/2/files/delete_v2", body, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
            return; // path/not_found: already gone.
        await EnsureSuccessAsync(response, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<long> GetDirectorySizeAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        long total = 0;
        foreach (var item in await ListAsync(path, cancellationToken))
            total += item.IsDirectory
                ? await GetDirectorySizeAsync(item.Path, cancellationToken)
                : item.Size;
        return total;
    }

    /// <summary>
    /// Releases locally owned resources. It does not revoke the Dropbox grant
    /// because disposal is also used for ordinary connection-cache cleanup.
    /// </summary>
    public Task Dispose()
    {
        _tokenLock.Dispose();
        if (_ownsHttpClient)
            _httpClient.Dispose();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await Dispose();

    private async Task UploadSmallFileAsync(
        string dropboxPath,
        string clientModified,
        Stream data,
        CancellationToken cancellationToken)
    {
        var arg = JsonSerializer.Serialize(new
        {
            path = dropboxPath,
            mode = "overwrite",
            autorename = false,
            mute = true,
            client_modified = clientModified
        });
        using var content = new StreamContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await SendContentAsync(
            "/2/files/upload", arg, content, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    /// <summary>Uploads a seekable stream through a Dropbox upload session.</summary>
    private async Task UploadLargeFileAsync(
        string dropboxPath,
        string clientModified,
        Stream data,
        CancellationToken cancellationToken)
    {
        var totalLength = data.Length - data.Position;
        long offset = 0;
        var buffer = new byte[UploadChunkSize];
        string? sessionId = null;

        while (offset < totalLength)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requested = (int)Math.Min(buffer.Length, totalLength - offset);
            var read = 0;
            while (read < requested)
            {
                var count = await data.ReadAsync(buffer.AsMemory(read, requested - read), cancellationToken);
                if (count == 0)
                    throw new EndOfStreamException("The upload source ended before its reported length.");
                read += count;
            }

            using var content = new ByteArrayContent(buffer, 0, read);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var isLastChunk = offset + read >= totalLength;

            if (sessionId is null)
            {
                var startArg = JsonSerializer.Serialize(new { close = false });
                using var startResponse = await SendContentAsync(
                    "/2/files/upload_session/start", startArg, content, cancellationToken);
                using var startJson = await ParseSuccessAsync(startResponse, cancellationToken);
                sessionId = startJson.RootElement.GetProperty("session_id").GetString()
                            ?? throw new InvalidOperationException("Dropbox did not return an upload session id.");
            }
            else if (!isLastChunk)
            {
                var appendArg = JsonSerializer.Serialize(new
                {
                    cursor = new { session_id = sessionId, offset },
                    close = false
                });
                using var appendResponse = await SendContentAsync(
                    "/2/files/upload_session/append_v2", appendArg, content, cancellationToken);
                await EnsureSuccessAsync(appendResponse, cancellationToken);
            }
            else
            {
                var finishArg = JsonSerializer.Serialize(new
                {
                    cursor = new { session_id = sessionId, offset },
                    commit = new
                    {
                        path = dropboxPath,
                        mode = "overwrite",
                        autorename = false,
                        mute = true,
                        client_modified = clientModified
                    }
                });
                using var finishResponse = await SendContentAsync(
                    "/2/files/upload_session/finish", finishArg, content, cancellationToken);
                await EnsureSuccessAsync(finishResponse, cancellationToken);
            }

            offset += read;
        }

        if (sessionId is null)
            throw new InvalidOperationException("The Dropbox upload did not complete.");
    }

    /// <summary>Sends an authenticated JSON RPC request to the Dropbox API host.</summary>
    private async Task<HttpResponseMessage> SendRpcAsync(
        string path,
        string? jsonBody,
        CancellationToken cancellationToken = default)
    {
        var token = await GetAccessTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiBase + path)
        {
            // Dropbox RPC endpoints require a JSON body; a null argument is the
            // literal JSON value null with the JSON content type.
            Content = new StringContent(jsonBody ?? "null", Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _httpClient.SendAsync(request, cancellationToken);
    }

    /// <summary>Sends an authenticated content-upload request with the JSON argument in the header.</summary>
    private async Task<HttpResponseMessage> SendContentAsync(
        string path,
        string apiArg,
        HttpContent content,
        CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, ContentBase + path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        // JsonSerializer escapes non-ASCII to \uXXXX, keeping the header value ASCII-safe.
        request.Headers.Add("Dropbox-API-Arg", apiArg);
        return await _httpClient.SendAsync(request, cancellationToken);
    }

    /// <summary>Returns a cached access token or serializes a refresh-token exchange.</summary>
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

            using var tokenTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            tokenTimeout.CancelAfter(TokenExchangeTimeout);
            using var response = await _httpClient.PostAsync(
                DropboxOAuthDefaults.TokenEndpoint,
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = _data["refreshToken"],
                    ["client_id"] = _identity.AppKey
                }),
                tokenTimeout.Token);
            using var json = await ParseSuccessAsync(response, tokenTimeout.Token);
            // A blank or absent access token would otherwise be sent as an empty
            // "Authorization: Bearer" header, which Dropbox rejects with an opaque
            // HTTP 400. Fail here with a clear cause instead.
            var accessToken = json.RootElement.TryGetProperty("access_token", out var accessTokenElement)
                ? accessTokenElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(accessToken))
                throw new InvalidOperationException("Dropbox did not return an access token.");
            var expiresIn = json.RootElement.TryGetProperty("expires_in", out var expiry)
                ? expiry.GetInt32()
                : 14400;
            _accessToken = accessToken;
            _accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
            return accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private static async Task<JsonDocument> ParseSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken = default)
    {
        await EnsureSuccessAsync(response, cancellationToken);
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
    }

    /// <summary>Converts unsuccessful Dropbox responses into a sanitized exception.</summary>
    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken = default)
    {
        if (response.IsSuccessStatusCode)
            return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new ProviderRequestException(
            "dropbox",
            ProviderErrorSanitizer.FromResponse(response.StatusCode, body));
    }

    /// <summary>Normalizes provider paths to slash-separated, root-relative form without slashes.</summary>
    private static string NormalizePath(string? path)
        => string.IsNullOrWhiteSpace(path) ? string.Empty : path.Replace('\\', '/').Trim('/');

    /// <summary>Converts a path to the Dropbox wire form: empty for the root, otherwise leading-slash.</summary>
    private static string DropboxPath(string? path)
    {
        var normalized = NormalizePath(path);
        return normalized.Length == 0 ? string.Empty : "/" + normalized;
    }

    private static string RequireFilePath(string path)
    {
        var normalized = NormalizePath(path);
        return normalized.Length == 0
            ? throw new ArgumentException("A Dropbox file path is required.", nameof(path))
            : normalized;
    }

    private static string CombinePath(string parent, string child)
        => NormalizePath(parent) is { Length: > 0 } normalizedParent
            ? $"/{normalizedParent}/{child}"
            : $"/{child}";

    private static string? GetOptionalString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;

    private static DateTime ParseDropboxDate(JsonElement element, string propertyName)
        => DateTimeOffset.TryParse(
            GetOptionalString(element, propertyName),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var value)
            ? value.UtcDateTime
            : DateTime.MinValue;
}

/// <summary>Shared Dropbox OAuth endpoints and least-privilege runtime scopes.</summary>
public static class DropboxOAuthDefaults
{
    /// <summary>
    /// Public Dropbox application key shipped with Kaimo. A Dropbox app key is a
    /// public identifier, not a secret. Replace this with the key of your own
    /// registered Dropbox app, or override it with <c>ExternalStorage:Dropbox:AppKey</c>.
    /// PKCE is used so no application secret is required.
    /// </summary>
    public const string DefaultAppKey = "wovr28naznylgb7";

    public const string AuthorizeEndpoint = "https://www.dropbox.com/oauth2/authorize";
    public const string TokenEndpoint = "https://api.dropboxapi.com/oauth2/token";

    /// <summary>Least-privilege scopes for browsing, sync, and virtual-share file access.</summary>
    public const string Scope =
        "account_info.read files.metadata.read files.content.read files.content.write";
}

/// <summary>
/// Resolves the public Dropbox application key used by PKCE authorization and
/// refresh-token exchanges. The default is Kaimo's shipped public app key; an
/// installation can supply its own key without a custom image.
/// </summary>
public sealed class DropboxIdentityConfiguration
{
    private DropboxIdentityConfiguration(string appKey) => AppKey = appKey;

    /// <summary>Public Dropbox application key. It is deliberately not a secret.</summary>
    public string AppKey { get; }

    /// <summary>True when an application key is available for authorization.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(AppKey);

    /// <summary>Reads the optional installation-local Dropbox application key.</summary>
    public static DropboxIdentityConfiguration FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var appKey = configuration["ExternalStorage:Dropbox:AppKey"];
        return Create(string.IsNullOrWhiteSpace(appKey) ? DropboxOAuthDefaults.DefaultAppKey : appKey);
    }

    /// <summary>
    /// Validates the application-key characters. An empty key is allowed so a
    /// deployment that does not use Dropbox still starts; authorization checks
    /// <see cref="IsConfigured"/> and fails with a clear message when it is unset.
    /// </summary>
    public static DropboxIdentityConfiguration Create(string? appKey)
    {
        var normalized = appKey?.Trim() ?? string.Empty;
        if (normalized.Length > 0 && !normalized.All(char.IsAsciiLetterOrDigit))
            throw new InvalidOperationException(
                "ExternalStorage:Dropbox:AppKey must be a Dropbox application key.");
        return new DropboxIdentityConfiguration(normalized);
    }
}

/// <summary>Dropbox registration for the provider-neutral cloud subsystem.</summary>
public sealed class DropboxProvider(DropboxIdentityConfiguration identity) : ICloudProvider
{
    public string Id => "dropbox";
    public string DisplayName => "Dropbox";
    public string AuthorizationEndpoint => "/api/cloud-access/dropbox/connect";

    /// <inheritdoc />
    public ICloudConnection CreateConnection(Guid shareId, SyncedFolder folder)
        => new DropboxConnection(folder.Data, identity: identity);
}
