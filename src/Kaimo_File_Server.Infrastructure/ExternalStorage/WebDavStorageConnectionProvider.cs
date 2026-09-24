using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.ExternalStorage;

namespace Kaimo_File_Server.Infrastructure.ExternalStorage;

public sealed class WebDavStorageConnectionProvider(
    ICredentialVault credentialVault) : IStorageConnectionProvider
{
    public string Id => "webdav";
    public string DisplayName => "WebDAV";
    public StorageProviderCapabilities Capabilities =>
        StorageProviderHelpers.ReadWriteCapabilities
        | StorageProviderCapabilities.DirectFileAccess;
    public IReadOnlySet<StorageAuthorizationMode> AuthorizationModes { get; }
        = new HashSet<StorageAuthorizationMode> { StorageAuthorizationMode.UsernamePassword };

    public Task<IStorageSession> OpenSessionAsync(
        StorageConnection connection,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WebDavConnectionSettings settings = ParseAndValidate(connection);
        Dictionary<string, string> credentials = credentialVault.UnprotectConnectionCredentials(connection);
        ValidateCredentials(credentials);
        HttpClient client = CreateHttpClient(settings, credentials["username"], credentials["password"]);
        var store = new WebDavRemoteFileStore(client, settings.ServerUrl);
        var session = new WebDavStorageSession(connection.Id, Capabilities, client, store);
        return Task.FromResult<IStorageSession>(session);
    }

    public async Task<StorageConnectionHealthResult> TestAsync(
        StorageConnection connection,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using IStorageSession session = await OpenSessionAsync(connection, cancellationToken);
            await session.RemoteFiles!.ListAsync("/", cancellationToken);
            return StorageProviderHelpers.Healthy();
        }
        catch (ProtocolConfigurationException exception)
        {
            return StorageProviderHelpers.Invalid(exception);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return StorageProviderHelpers.Unavailable("webdav_connection_failed");
        }
        catch (IOException)
        {
            return StorageProviderHelpers.Unavailable("webdav_connection_failed");
        }
    }

    public Task RevokeAsync(StorageConnection connection, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    internal static WebDavConnectionSettings ParseAndValidate(StorageConnection connection)
    {
        StorageProviderHelpers.ValidateConnection(connection, "webdav", StorageAuthorizationMode.UsernamePassword);
        var settings = ProtocolConnectionSettings.Parse<WebDavConnectionSettings>(connection.SettingsJson, "WebDAV");
        if (string.IsNullOrWhiteSpace(settings.ServerUrl)
            || settings.ServerUrl.Length > 2048
            || !Uri.TryCreate(settings.ServerUrl, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("https" or "http"))
            throw new ProtocolConfigurationException("server_url_invalid", "The WebDAV server URL is invalid.");
        return settings;
    }

    internal static void ValidateCredentials(Dictionary<string, string> credentials)
    {
        if (!credentials.TryGetValue("username", out string? username)
            || string.IsNullOrWhiteSpace(username)
            || username.Length > 256
            || username.IndexOfAny(['\r', '\n']) >= 0
            || !credentials.TryGetValue("password", out string? password)
            || password.Length > 4096
            || password.IndexOfAny(['\r', '\n']) >= 0)
            throw new ProtocolConfigurationException("credentials_invalid", "The WebDAV credentials are invalid.");
    }

    internal static HttpClient CreateHttpClient(
        WebDavConnectionSettings settings,
        string username,
        string password)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(120)
        };
        byte[] credentialBytes = Encoding.UTF8.GetBytes($"{username}:{password}");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(credentialBytes));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        return client;
    }
}

internal sealed class WebDavStorageSession(
    Guid connectionId,
    StorageProviderCapabilities capabilities,
    HttpClient client,
    IRemoteFileStore remoteFiles) : IStorageSession
{
    public Guid ConnectionId { get; } = connectionId;
    public StorageProviderCapabilities Capabilities { get; } = capabilities;
    public IRemoteFileStore RemoteFiles { get; } = remoteFiles;
    IRemoteFileStore? IStorageSession.RemoteFiles => RemoteFiles;
    public IOptimizedStorageSync? OptimizedSync => null;

    public ValueTask DisposeAsync()
    {
        client.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class WebDavRemoteFileStore(HttpClient client, string serverUrl) : IRemoteFileStore
{
    private static readonly XNamespace DavNamespace = "DAV:";

    public async Task<IReadOnlyList<RemoteStorageItem>> ListAsync(
        string path, CancellationToken cancellationToken = default)
    {
        string normalizedPath = WebDavPaths.NormalizeStorePath(path);
        Uri requestUri = ResolveUri(normalizedPath, trailingSlash: true);

        const string propfindBody = """
            <?xml version="1.0" encoding="utf-8"?>
            <d:propfind xmlns:d="DAV:">
              <d:prop>
                <d:resourcetype/>
                <d:getcontentlength/>
                <d:getlastmodified/>
                <d:displayname/>
              </d:prop>
            </d:propfind>
            """;

        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), requestUri)
        {
            Content = new StringContent(propfindBody, Encoding.UTF8, "application/xml")
        };
        request.Headers.Add("Depth", "1");

        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        await EnsureSuccessOrThrowAsync(response, cancellationToken);

        string xml = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseMultiStatusResponse(xml, normalizedPath);
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        Uri requestUri = ResolveFileUri(path);
        HttpResponseMessage response = await client.GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        try
        {
            await EnsureSuccessOrThrowAsync(response, cancellationToken);
            Stream contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return new ResponseOwningStream(contentStream, response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public async Task WriteAsync(
        string path, Stream content, bool overwrite, CancellationToken cancellationToken = default)
    {
        Uri requestUri = ResolveFileUri(path);
        using var request = new HttpRequestMessage(HttpMethod.Put, requestUri)
        {
            Content = new StreamContent(content)
        };
        if (!overwrite)
            request.Headers.Add("If-None-Match", "*");
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        await EnsureSuccessOrThrowAsync(response, cancellationToken);
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        Uri requestUri = ResolveFileUri(path, trailingSlash: true);
        using var request = new HttpRequestMessage(new HttpMethod("MKCOL"), requestUri);
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        await EnsureSuccessOrThrowAsync(response, cancellationToken);
    }

    public async Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken = default)
    {
        Uri requestUri = ResolveFileUri(path);
        using var request = new HttpRequestMessage(HttpMethod.Delete, requestUri);
        if (!recursive)
            request.Headers.Add("Depth", "0");
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        await EnsureSuccessOrThrowAsync(response, cancellationToken);
    }

    public async Task MoveAsync(
        string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        Uri sourceUri = ResolveFileUri(sourcePath);
        Uri destinationUri = ResolveFileUri(destinationPath);
        using var request = new HttpRequestMessage(new HttpMethod("MOVE"), sourceUri);
        request.Headers.Add("Destination", destinationUri.AbsoluteUri);
        request.Headers.Add("Overwrite", "F");
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        await EnsureSuccessOrThrowAsync(response, cancellationToken);
    }

    internal IReadOnlyList<RemoteStorageItem> ParseMultiStatusResponse(string xml, string parentPath)
    {
        XDocument document = XDocument.Parse(xml);
        var items = new List<RemoteStorageItem>();
        Uri parentUri = ResolveUri(parentPath, trailingSlash: true);
        string parentHref = Uri.UnescapeDataString(parentUri.AbsolutePath).TrimEnd('/');

        foreach (XElement responseElement in document.Descendants(DavNamespace + "response"))
        {
            string? href = responseElement.Element(DavNamespace + "href")?.Value;
            if (string.IsNullOrWhiteSpace(href))
                continue;

            string decodedHref = Uri.UnescapeDataString(href).TrimEnd('/');
            if (string.Equals(decodedHref, parentHref, StringComparison.OrdinalIgnoreCase))
                continue;

            XElement? propStat = responseElement
                .Elements(DavNamespace + "propstat")
                .FirstOrDefault(ps =>
                {
                    string? status = ps.Element(DavNamespace + "status")?.Value;
                    return status is not null && status.Contains("200");
                });
            if (propStat is null)
                continue;

            XElement? prop = propStat.Element(DavNamespace + "prop");
            if (prop is null)
                continue;

            bool isDirectory = prop.Element(DavNamespace + "resourcetype")
                ?.Element(DavNamespace + "collection") is not null;

            string name = ExtractName(decodedHref);
            if (string.IsNullOrEmpty(name) || name is "." or "..")
                continue;

            string childPath = WebDavPaths.CombineChild(parentPath, name);

            long? size = null;
            if (!isDirectory)
            {
                string? contentLength = prop.Element(DavNamespace + "getcontentlength")?.Value;
                if (long.TryParse(contentLength, out long parsedSize))
                    size = parsedSize;
            }

            DateTime? modified = null;
            string? lastModified = prop.Element(DavNamespace + "getlastmodified")?.Value;
            if (lastModified is not null && DateTimeOffset.TryParse(lastModified, out DateTimeOffset parsedDate))
                modified = parsedDate.UtcDateTime;

            items.Add(new RemoteStorageItem(name, childPath, isDirectory, size, modified));
        }

        return items;
    }

    private static string ExtractName(string decodedHref)
    {
        string trimmed = decodedHref.TrimEnd('/');
        int lastSlash = trimmed.LastIndexOf('/');
        return lastSlash >= 0 ? trimmed[(lastSlash + 1)..] : trimmed;
    }

    internal Uri ResolveUri(string normalizedPath, bool trailingSlash = false)
    {
        string baseUrl = serverUrl.TrimEnd('/');
        // Store paths are decoded names; each segment must be percent-encoded, otherwise
        // '#', '?' or '%' in a file name would be parsed as URL syntax and the request
        // (e.g. a DELETE during sync) would target a different remote resource.
        string resolved = normalizedPath == "/"
            ? baseUrl + "/"
            : baseUrl + string.Join('/', normalizedPath.Split('/').Select(Uri.EscapeDataString))
              + (trailingSlash ? "/" : "");
        return new Uri(resolved);
    }

    private Uri ResolveFileUri(string path, bool trailingSlash = false)
    {
        string normalizedPath = WebDavPaths.NormalizeStorePath(path);
        if (normalizedPath == "/")
            throw new ProtocolConfigurationException("remote_path_invalid", "The remote root is not a file path.");
        return ResolveUri(normalizedPath, trailingSlash);
    }

    internal static async Task EnsureSuccessOrThrowAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.MultiStatus)
            return;

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new RemoteStorageAccessDeniedException(
                    $"WebDAV server returned {(int)response.StatusCode}: access denied."),
            HttpStatusCode.NotFound =>
                new IOException($"WebDAV resource not found: {response.RequestMessage?.RequestUri}"),
            HttpStatusCode.Conflict =>
                new IOException("WebDAV server returned 409 Conflict. A parent directory may not exist."),
            HttpStatusCode.PreconditionFailed =>
                new IOException("The remote item already exists."),
            HttpStatusCode.InsufficientStorage =>
                new IOException("WebDAV server returned 507 Insufficient Storage."),
            _ => new IOException(
                $"WebDAV request failed with status {(int)response.StatusCode}: {body[..Math.Min(body.Length, 500)]}")
        };
    }
}

internal sealed class ResponseOwningStream(Stream inner, HttpResponseMessage response) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => inner.ReadAsync(buffer, offset, count, cancellationToken);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => inner.ReadAsync(buffer, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
            response.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        response.Dispose();
        await base.DisposeAsync();
    }
}

internal static class WebDavPaths
{
    public static string NormalizeStorePath(string? path)
    {
        string value = (path ?? string.Empty).Replace('\\', '/').Trim();
        if (value.Length > 4096 || value.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new ProtocolConfigurationException("remote_path_invalid", "The remote path is invalid.");
        string[] segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
            throw new ProtocolConfigurationException("remote_path_invalid", "The remote path is invalid.");
        return segments.Length == 0 ? "/" : "/" + string.Join('/', segments);
    }

    public static string CombineChild(string normalizedStorePath, string childName)
        => normalizedStorePath == "/" ? "/" + childName : normalizedStorePath + "/" + childName;
}
