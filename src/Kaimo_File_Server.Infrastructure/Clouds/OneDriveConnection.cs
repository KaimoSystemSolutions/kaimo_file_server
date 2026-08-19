using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Services;

namespace Kaimo_File_Server.Infrastructure.Clouds;

/// <summary>
/// Provider-neutral OneDrive connection backed by Microsoft Graph. No Graph SDK
/// type crosses the cloud abstraction, so the UI and sync engine remain reusable.
/// </summary>
public sealed class OneDriveConnection : ICloudConnection, IAsyncDisposable
{
    private const long SimpleUploadThreshold = 10L * 1024 * 1024;
    private const int UploadChunkSize = 10 * 1024 * 1024; // 32 * Graph's 320 KiB fragment unit.
    private const string GraphBaseUrl = "https://graph.microsoft.com/v1.0";
    private static readonly TimeSpan TokenExchangeTimeout = TimeSpan.FromSeconds(60);

    private readonly Dictionary<string, string> _data;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Func<CancellationToken, Task<IAsyncDisposable?>>? _acquireRefreshLease;
    private readonly Func<CancellationToken, Task<Dictionary<string, string>>>? _reloadCredentials;
    private readonly Func<Dictionary<string, string>, CancellationToken, Task>? _persistRotatedCredentials;
    private readonly MicrosoftIdentityConfiguration _identity;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt;
    private bool _hasPendingCredentialChanges;

    /// <summary>
    /// Creates a Graph connection from the mapping's persisted refresh token.
    /// The optional HTTP client keeps provider behavior directly testable.
    /// </summary>
    public OneDriveConnection(
        Dictionary<string, string> data,
        HttpClient? httpClient = null,
        Func<CancellationToken, Task<IAsyncDisposable?>>? acquireRefreshLease = null,
        Func<CancellationToken, Task<Dictionary<string, string>>>? reloadCredentials = null,
        Func<Dictionary<string, string>, CancellationToken, Task>? persistRotatedCredentials = null,
        MicrosoftIdentityConfiguration? identity = null)
    {
        _data = data;
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _acquireRefreshLease = acquireRefreshLease;
        _reloadCredentials = reloadCredentials;
        _persistRotatedCredentials = persistRotatedCredentials;
        _identity = identity ?? MicrosoftIdentityConfiguration.Create(
            OneDriveOAuthDefaults.ClientId, OneDriveOAuthDefaults.Authority);

        if (!_data.TryGetValue("refreshToken", out var refreshToken)
            || string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidOperationException("The Microsoft OneDrive refresh token is missing.");
        }

        if (!_data.ContainsKey("scope"))
            _data["scope"] = OneDriveOAuthDefaults.Scope;
    }

    public string ServiceName => "Microsoft OneDrive";
    public bool HasPendingCredentialChanges => _hasPendingCredentialChanges;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> GetPendingCredentialChanges()
        => _hasPendingCredentialChanges
            ? new Dictionary<string, string> { ["refreshToken"] = _data["refreshToken"] }
            : new Dictionary<string, string>();

    /// <inheritdoc />
    public void AcknowledgeCredentialChanges() => _hasPendingCredentialChanges = false;

    /// <inheritdoc />
    public async Task<CloudAccountInfo?> GetAccountInfoAsync()
    {
        using var response = await SendGraphAsync(
            HttpMethod.Get,
            "/me?$select=displayName,mail,userPrincipalName");
        using var json = await ParseSuccessAsync(response);
        var root = json.RootElement;

        var displayName = GetOptionalString(root, "displayName");
        var email = GetOptionalString(root, "mail")
                    ?? GetOptionalString(root, "userPrincipalName");
        return new CloudAccountInfo(displayName, email, null);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Follows trusted Graph pagination links and omits Personal Vault because
    /// Microsoft exposes it in listings without making it normally downloadable.
    /// </remarks>
    public async Task<IReadOnlyList<CloudItemMeta>> ListAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePath(path);
        var requestUrl = normalizedPath.Length == 0
            ? "/me/drive/root/children?$select=id,name,size,lastModifiedDateTime,folder,file,package,specialFolder"
            : $"/me/drive/root:/{EncodePath(normalizedPath)}:/children" +
              "?$select=id,name,size,lastModifiedDateTime,folder,file,package,specialFolder";
        var result = new List<CloudItemMeta>();

        while (!string.IsNullOrEmpty(requestUrl))
        {
            using var response = await SendGraphAsync(
                HttpMethod.Get,
                requestUrl,
                cancellationToken: cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return [];

            using var json = await ParseSuccessAsync(response, cancellationToken);
            foreach (var item in json.RootElement.GetProperty("value").EnumerateArray())
            {
                // Microsoft Graph may expose Personal Vault in the root even while it
                // is locked and cannot be accessed through the normal Drive API. Do
                // not present it to the generic sync engine, which would otherwise
                // mistake facet-less vault responses for downloadable files.
                if (IsPersonalVault(item))
                    continue;

                var name = item.GetProperty("name").GetString() ?? "";
                var isDirectory = item.TryGetProperty("folder", out _)
                                  || item.TryGetProperty("package", out _)
                                  || item.TryGetProperty("specialFolder", out _);
                var size = item.TryGetProperty("size", out var sizeProperty)
                    ? sizeProperty.GetInt64()
                    : 0L;
                var modifiedAt = item.TryGetProperty("lastModifiedDateTime", out var modifiedProperty)
                    && DateTimeOffset.TryParse(
                        modifiedProperty.GetString(),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal,
                        out var parsedModified)
                        ? parsedModified.UtcDateTime
                        : DateTime.MinValue;

                result.Add(new CloudItemMeta(
                    isDirectory,
                    name,
                    CombinePath(normalizedPath, name),
                    size,
                    modifiedAt));
            }

            requestUrl = GetOptionalString(json.RootElement, "@odata.nextLink") ?? "";
        }

        return result;
    }

    /// <summary>Lists browser metadata including stable Graph ids.</summary>
    public async Task<IReadOnlyList<OneDriveItemMeta>> ListDetailedAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePath(path);
        var requestUrl = normalizedPath.Length == 0
            ? "/me/drive/root/children?$select=id,name,size,createdDateTime,lastModifiedDateTime,folder,file,package,specialFolder,eTag"
            : $"/me/drive/root:/{EncodePath(normalizedPath)}:/children" +
              "?$select=id,name,size,createdDateTime,lastModifiedDateTime,folder,file,package,specialFolder,eTag";
        var result = new List<OneDriveItemMeta>();

        while (!string.IsNullOrEmpty(requestUrl))
        {
            using var response = await SendGraphAsync(HttpMethod.Get, requestUrl, cancellationToken: cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return [];
            using var json = await ParseSuccessAsync(response, cancellationToken);
            foreach (var item in json.RootElement.GetProperty("value").EnumerateArray())
            {
                if (IsPersonalVault(item)) continue;
                var name = GetOptionalString(item, "name") ?? string.Empty;
                var isDirectory = item.TryGetProperty("folder", out _)
                                  || item.TryGetProperty("package", out _)
                                  || item.TryGetProperty("specialFolder", out _);
                result.Add(new OneDriveItemMeta(
                    GetOptionalString(item, "id") ?? throw new InvalidOperationException("Microsoft Graph did not return an item id."),
                    name,
                    CombinePath(normalizedPath, name).TrimStart('/'),
                    isDirectory,
                    item.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
                    ParseGraphDate(item, "createdDateTime"),
                    ParseGraphDate(item, "lastModifiedDateTime"),
                    GetOptionalString(item, "eTag")));
            }
            requestUrl = GetOptionalString(json.RootElement, "@odata.nextLink") ?? string.Empty;
        }
        return result;
    }

    /// <summary>
    /// Two-way delete propagation entry point. A Graph delete removes folders
    /// recursively, so the <paramref name="isDirectory"/> hint is not needed here.
    /// </summary>
    public Task DeleteAsync(string path, bool isDirectory, CancellationToken cancellationToken = default)
        => DeleteItemAsync(path, cancellationToken);

    public async Task DeleteItemAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalizedPath = RequireFilePath(path);
        using var response = await SendGraphAsync(
            HttpMethod.Delete,
            $"/me/drive/root:/{EncodePath(normalizedPath)}",
            cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<OneDriveFolderReference> ResolveFolderAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizePath(path);
        if (normalized.Length == 0)
            return new OneDriveFolderReference(await GetRootIdAsync(cancellationToken), string.Empty);
        var item = await GetItemByPathAsync(normalized, cancellationToken)
                   ?? throw new DirectoryNotFoundException("The OneDrive folder was not found.");
        if (!item.IsDirectory)
            throw new IOException("The selected OneDrive item is not a folder.");
        return new OneDriveFolderReference(item.Id, normalized);
    }

    public async Task<OneDriveFolderReference> ResolveFolderByIdAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            throw new ArgumentException("A OneDrive folder id is required.", nameof(providerId));
        using var response = await SendGraphAsync(
            HttpMethod.Get,
            $"/me/drive/items/{Uri.EscapeDataString(providerId)}?$select=id,name,parentReference,folder,root",
            cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new DirectoryNotFoundException("The configured OneDrive root folder no longer exists.");
        using var json = await ParseSuccessAsync(response, cancellationToken);
        var item = json.RootElement;
        if (!item.TryGetProperty("folder", out _) && !item.TryGetProperty("root", out _))
            throw new IOException("The configured OneDrive root item is no longer a folder.");
        if (item.TryGetProperty("root", out _))
            return new OneDriveFolderReference(providerId, string.Empty);
        var name = GetOptionalString(item, "name")
                   ?? throw new InvalidOperationException("Microsoft Graph did not return the folder name.");
        var parentPath = item.TryGetProperty("parentReference", out var parent)
            ? GetOptionalString(parent, "path") ?? string.Empty
            : string.Empty;
        const string rootMarker = "/root:";
        var markerIndex = parentPath.IndexOf(rootMarker, StringComparison.OrdinalIgnoreCase);
        var relativeParent = markerIndex >= 0
            ? parentPath[(markerIndex + rootMarker.Length)..].Trim('/')
            : string.Empty;
        return new OneDriveFolderReference(providerId, ShareRelativePathForGraph(relativeParent, name));
    }

    public async Task MoveItemAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var source = await GetItemByPathAsync(RequireFilePath(sourcePath), cancellationToken)
                     ?? throw new FileNotFoundException("The OneDrive item was not found.", sourcePath);
        var destination = RequireFilePath(destinationPath);
        var separator = destination.LastIndexOf('/');
        var parentPath = separator < 0 ? string.Empty : destination[..separator];
        var name = separator < 0 ? destination : destination[(separator + 1)..];
        var parentId = parentPath.Length == 0
            ? await GetRootIdAsync(cancellationToken)
            : await EnsureDirectoryAsync(parentPath, cancellationToken);
        var body = JsonSerializer.Serialize(new { name, parentReference = new { id = parentId } });
        using var response = await SendGraphAsync(
            HttpMethod.Patch,
            $"/me/drive/items/{Uri.EscapeDataString(source.Id)}",
            new StringContent(body, Encoding.UTF8, "application/json"),
            cancellationToken: cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    /// <summary>Starts a provider-side copy; file bytes do not traverse this server.</summary>
    public async Task CopyItemAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var source = await GetItemByPathAsync(RequireFilePath(sourcePath), cancellationToken)
                     ?? throw new FileNotFoundException("The OneDrive item was not found.", sourcePath);
        var destination = RequireFilePath(destinationPath);
        var separator = destination.LastIndexOf('/');
        var parentPath = separator < 0 ? string.Empty : destination[..separator];
        var name = separator < 0 ? destination : destination[(separator + 1)..];
        var parentId = parentPath.Length == 0
            ? await GetRootIdAsync(cancellationToken)
            : await EnsureDirectoryAsync(parentPath, cancellationToken);
        var body = JsonSerializer.Serialize(new { name, parentReference = new { id = parentId } });
        using var response = await SendGraphAsync(
            HttpMethod.Post,
            $"/me/drive/items/{Uri.EscapeDataString(source.Id)}/copy",
            new StringContent(body, Encoding.UTF8, "application/json"),
            cancellationToken: cancellationToken);
        if (response.StatusCode != HttpStatusCode.Accepted)
        {
            await EnsureSuccessAsync(response, cancellationToken);
            return;
        }

        var monitor = response.Headers.Location
                      ?? throw new InvalidOperationException("Microsoft Graph did not return a copy monitor URL.");
        ValidateCopyMonitorUri(monitor);
        await WaitForCopyCompletionAsync(monitor, cancellationToken);
    }

    private async Task WaitForCopyCompletionAsync(Uri monitor, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, monitor);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.RequestMessage?.RequestUri is { } finalUri)
                ValidateCopyMonitorUri(finalUri);
            await EnsureSuccessAsync(response, cancellationToken);
            if (response.Content.Headers.ContentLength == 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                continue;
            }
            using var json = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            var root = json.RootElement;
            var percentage = root.TryGetProperty("percentageComplete", out var firstPercentage)
                ? firstPercentage.GetDouble()
                : root.TryGetProperty("percentComplete", out var secondPercentage)
                    ? secondPercentage.GetDouble()
                    : -1;
            if (percentage >= 100) return;
            var status = GetOptionalString(root, "status");
            if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)) return;
            if (status is not null
                && !string.Equals(status, "inProgress", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(status, "notStarted", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(status, "running", StringComparison.OrdinalIgnoreCase))
                throw new IOException($"The OneDrive copy operation ended with status '{status}'.");
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
        throw new TimeoutException("The OneDrive copy operation did not complete within 15 minutes.");
    }

    private static void ValidateCopyMonitorUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !uri.IsDefaultPort
            || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(uri.Host, out _))
            throw new InvalidOperationException("Microsoft Graph returned an untrusted copy monitor URL.");
    }

    /// <summary>
    /// Detects Personal Vault by its locale-independent Graph facet. Display
    /// names such as "Personal Vault" or "Persönlicher Tresor" are not stable.
    /// </summary>
    private static bool IsPersonalVault(JsonElement item)
    {
        if (!item.TryGetProperty("specialFolder", out var specialFolder)
            || specialFolder.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return string.Equals(
            GetOptionalString(specialFolder, "name"),
            "vault",
            StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public async Task DownloadAsync(
        string path,
        Stream target,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = RequireFilePath(path);
        using var response = await SendGraphAsync(
            HttpMethod.Get,
            $"/me/drive/root:/{EncodePath(normalizedPath)}:/content",
            completion: HttpCompletionOption.ResponseHeadersRead,
            cancellationToken: cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new FileNotFoundException("The OneDrive file was not found.", path);

        await EnsureSuccessAsync(response, cancellationToken);
        await response.Content.CopyToAsync(target, cancellationToken);
    }

    /// <inheritdoc />
    public async Task CreateDirectoryAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePath(path);
        if (normalizedPath.Length == 0)
            return;

        await EnsureDirectoryAsync(normalizedPath, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Small files use direct upload; large seekable streams use a chunked,
    /// preauthenticated upload session. Both paths honor cancellation.
    /// </remarks>
    public async Task UploadAsync(
        string path,
        Stream data,
        DateTime modifiedTime,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = RequireFilePath(path);
        var separator = normalizedPath.LastIndexOf('/');
        if (separator >= 0)
            await EnsureDirectoryAsync(normalizedPath[..separator], cancellationToken);

        string itemId;
        if (data.CanSeek && data.Length - data.Position > SimpleUploadThreshold)
            itemId = await UploadLargeFileAsync(normalizedPath, data, cancellationToken);
        else
            itemId = await UploadSmallFileAsync(normalizedPath, data, cancellationToken);

        // Preserve the source timestamp; the generic two-way sync compares it
        // to decide which side is newer on the next run.
        var body = JsonSerializer.Serialize(new
        {
            fileSystemInfo = new
            {
                lastModifiedDateTime = modifiedTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            }
        });
        using var response = await SendGraphAsync(
            HttpMethod.Patch,
            $"/me/drive/items/{Uri.EscapeDataString(itemId)}",
            new StringContent(body, Encoding.UTF8, "application/json"),
            cancellationToken: cancellationToken);
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
    /// Releases locally owned resources. It does not revoke the user's Microsoft
    /// grant because connection disposal is also used for ordinary cache cleanup.
    /// </summary>
    public Task Dispose()
    {
        _tokenLock.Dispose();
        if (_ownsHttpClient)
            _httpClient.Dispose();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await Dispose();

    /// <summary>
    /// Resolves or creates every segment of a remote directory path. Conflict
    /// responses are re-read to handle concurrent folder creation safely.
    /// </summary>
    private async Task<string> EnsureDirectoryAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var parentId = await GetRootIdAsync(cancellationToken);
        var currentPath = "";

        foreach (var segment in NormalizePath(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            currentPath = CombinePath(currentPath, segment);
            var existing = await GetItemByPathAsync(currentPath, cancellationToken);
            if (existing is not null)
            {
                if (!existing.Value.IsDirectory)
                    throw new IOException($"A OneDrive file already exists at '{currentPath}'.");
                parentId = existing.Value.Id;
                continue;
            }

            var body = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["name"] = segment,
                ["folder"] = new { },
                ["@microsoft.graph.conflictBehavior"] = "fail"
            });
            using var response = await SendGraphAsync(
                HttpMethod.Post,
                $"/me/drive/items/{Uri.EscapeDataString(parentId)}/children",
                new StringContent(body, Encoding.UTF8, "application/json"),
                cancellationToken: cancellationToken);

            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                existing = await GetItemByPathAsync(currentPath, cancellationToken);
                if (existing is not { IsDirectory: true } concurrentFolder)
                {
                    await EnsureSuccessAsync(response, cancellationToken);
                    throw new IOException($"Unable to create the OneDrive folder '{currentPath}'.");
                }

                parentId = concurrentFolder.Id;
                continue;
            }

            using var json = await ParseSuccessAsync(response, cancellationToken);
            parentId = json.RootElement.GetProperty("id").GetString()
                       ?? throw new InvalidOperationException("Microsoft Graph did not return a folder id.");
        }

        return parentId;
    }

    /// <summary>Uploads a file with Graph's single-request content endpoint.</summary>
    private async Task<string> UploadSmallFileAsync(
        string path,
        Stream data,
        CancellationToken cancellationToken)
    {
        using var content = new StreamContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await SendGraphAsync(
            HttpMethod.Put,
            $"/me/drive/root:/{EncodePath(path)}:/content",
            content,
            cancellationToken: cancellationToken);
        using var json = await ParseSuccessAsync(response, cancellationToken);
        return json.RootElement.GetProperty("id").GetString()
               ?? throw new InvalidOperationException("Microsoft Graph did not return a file id.");
    }

    /// <summary>
    /// Uploads a seekable stream in Graph-compatible fragments. Upload-session
    /// URLs are preauthenticated and therefore intentionally receive no bearer token.
    /// </summary>
    private async Task<string> UploadLargeFileAsync(
        string path,
        Stream data,
        CancellationToken cancellationToken)
    {
        var sessionBody = JsonSerializer.Serialize(new
        {
            item = new Dictionary<string, object>
            {
                ["@microsoft.graph.conflictBehavior"] = "replace",
                ["name"] = path.Split('/')[^1]
            }
        });
        using var sessionResponse = await SendGraphAsync(
            HttpMethod.Post,
            $"/me/drive/root:/{EncodePath(path)}:/createUploadSession",
            new StringContent(sessionBody, Encoding.UTF8, "application/json"),
            cancellationToken: cancellationToken);
        using var sessionJson = await ParseSuccessAsync(sessionResponse, cancellationToken);
        var uploadUrl = sessionJson.RootElement.GetProperty("uploadUrl").GetString()
                        ?? throw new InvalidOperationException("Microsoft Graph did not return an upload URL.");

        var totalLength = data.Length - data.Position;
        long uploaded = 0;
        var buffer = new byte[UploadChunkSize];

        while (uploaded < totalLength)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requested = (int)Math.Min(buffer.Length, totalLength - uploaded);
            var read = 0;
            while (read < requested)
            {
                var count = await data.ReadAsync(
                    buffer.AsMemory(read, requested - read),
                    cancellationToken);
                if (count == 0)
                    throw new EndOfStreamException("The upload source ended before its reported length.");
                read += count;
            }

            using var content = new ByteArrayContent(buffer, 0, read);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentRange = new ContentRangeHeaderValue(
                uploaded,
                uploaded + read - 1,
                totalLength);
            using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = content };
            // Upload-session URLs are preauthenticated and must not receive the
            // Microsoft Graph Authorization header.
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            uploaded += read;

            if (uploaded == totalLength)
            {
                await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var json = await JsonDocument.ParseAsync(
                    responseStream,
                    cancellationToken: cancellationToken);
                return json.RootElement.GetProperty("id").GetString()
                       ?? throw new InvalidOperationException("Microsoft Graph did not return a file id.");
            }
        }

        throw new InvalidOperationException("The OneDrive upload did not complete.");
    }

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

    private readonly record struct DriveItemReference(string Id, bool IsDirectory);
}

public sealed record OneDriveItemMeta(
    string ProviderId,
    string Name,
    string Path,
    bool IsDirectory,
    long Size,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc,
    string? ETag);

public sealed record OneDriveFolderReference(string ProviderId, string Path);

/// <summary>Shared Microsoft identity endpoints and least-privilege runtime scopes.</summary>
public static class OneDriveOAuthDefaults
{
    public const string ClientId = "e966f5be-e8a1-4c67-b322-aac34c1ab642";
    public const string Authority = "common";
    public const string Scope = "offline_access Files.ReadWrite User.Read";
    public const string DeviceCodeEndpoint =
        "https://login.microsoftonline.com/common/oauth2/v2.0/devicecode";
    public const string TokenEndpoint =
        "https://login.microsoftonline.com/common/oauth2/v2.0/token";
}
