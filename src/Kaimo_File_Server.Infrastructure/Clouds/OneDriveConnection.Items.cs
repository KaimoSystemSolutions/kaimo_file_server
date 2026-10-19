using System.Net;
using System.Text;
using System.Text.Json;

namespace Kaimo_File_Server.Infrastructure.Clouds;

public partial class OneDriveConnection
{
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
}
