using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Kaimo_File_Server.Core.Services.DataServices;

namespace Kaimo_File_Server.Infrastructure.Clouds;

/// <summary>
/// Provider-neutral OneDrive connection backed by Microsoft Graph. No Graph SDK
/// type crosses the cloud abstraction, so the UI and sync engine remain reusable.
/// </summary>
public sealed partial class OneDriveConnection : ICloudConnection, IAsyncDisposable
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

    private readonly record struct DriveItemReference(string Id, bool IsDirectory);
}
