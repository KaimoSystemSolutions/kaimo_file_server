using System.Net;
using System.Text;
using System.Xml.Linq;
using Kaimo_File_Server.Core.Services.ExternalStorage;

namespace Kaimo_File_Server.Infrastructure.ExternalStorage;

internal sealed class WebDavRemoteFileStore(
    HttpClient client, string serverUrl, TimeSpan? idleTimeout = null) : IRemoteFileStore
{
    private static readonly XNamespace DavNamespace = "DAV:";

    /// <summary>
    /// Longest silence tolerated from the server: for plain requests until the
    /// response arrives, for uploads between two chunks read from the source, so
    /// a multi-GB PUT is not cut off while a stalled connection still fails.
    /// </summary>
    internal static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(120);
    private readonly TimeSpan _idleTimeout = idleTimeout ?? DefaultIdleTimeout;

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

        using HttpResponseMessage response = await SendAsync(request, cancellationToken);
        await EnsureSuccessOrThrowAsync(response, cancellationToken);

        string xml = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseMultiStatusResponse(xml, normalizedPath);
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        Uri requestUri = ResolveFileUri(path);
        HttpResponseMessage response = await SendAsync(
            new HttpRequestMessage(HttpMethod.Get, requestUri), cancellationToken, HttpCompletionOption.ResponseHeadersRead);
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
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(_idleTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Put, requestUri)
        {
            Content = new StreamContent(new IdleTimeoutReadStream(content, idle, _idleTimeout))
        };
        if (!overwrite)
            request.Headers.Add("If-None-Match", "*");
        using HttpResponseMessage response = await SendAsync(request, cancellationToken, idle: idle);
        await EnsureSuccessOrThrowAsync(response, cancellationToken);
    }

    /// <summary>
    /// Sets the mtime the way the Windows WebDAV redirector does (PROPPATCH
    /// <c>Win32LastModifiedTime</c>), honored by IIS and Kaimo itself. Servers that store it
    /// only as a dead property (e.g. Nextcloud) keep the upload time.
    /// </summary>
    public Task TrySetModifiedTimeAsync(
        string path, DateTime modifiedUtc, CancellationToken cancellationToken = default)
        => ProppatchModifiedTimeAsync(ResolveFileUri(path), modifiedUtc, cancellationToken);

    /// <summary>
    /// Folder variant: a collection URI ends with a slash (Apache and nginx answer a
    /// slash-less PROPPATCH on a folder with a 301 redirect).
    /// </summary>
    public Task TrySetDirectoryModifiedTimeAsync(
        string path, DateTime modifiedUtc, CancellationToken cancellationToken = default)
        => ProppatchModifiedTimeAsync(ResolveFileUri(path, trailingSlash: true), modifiedUtc, cancellationToken);

    private async Task ProppatchModifiedTimeAsync(
        Uri requestUri, DateTime modifiedUtc, CancellationToken cancellationToken)
    {
        string body = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <d:propertyupdate xmlns:d="DAV:" xmlns:z="urn:schemas-microsoft-com:">
              <d:set><d:prop><z:Win32LastModifiedTime>{modifiedUtc.ToUniversalTime():R}</z:Win32LastModifiedTime></d:prop></d:set>
            </d:propertyupdate>
            """;
        using var request = new HttpRequestMessage(new HttpMethod("PROPPATCH"), requestUri)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/xml")
        };
        using HttpResponseMessage response = await SendAsync(request, cancellationToken);
        await EnsureSuccessOrThrowAsync(response, cancellationToken);
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        Uri requestUri = ResolveFileUri(path, trailingSlash: true);
        using var request = new HttpRequestMessage(new HttpMethod("MKCOL"), requestUri);
        using HttpResponseMessage response = await SendAsync(request, cancellationToken);
        await EnsureSuccessOrThrowAsync(response, cancellationToken);
    }

    public async Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken = default)
    {
        Uri requestUri = ResolveFileUri(path);
        using var request = new HttpRequestMessage(HttpMethod.Delete, requestUri);
        if (!recursive)
            request.Headers.Add("Depth", "0");
        using HttpResponseMessage response = await SendAsync(request, cancellationToken);
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
        using HttpResponseMessage response = await SendAsync(request, cancellationToken);
        await EnsureSuccessOrThrowAsync(response, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead,
        CancellationTokenSource? idle = null)
    {
        using var timeout = idle is null ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) : null;
        timeout?.CancelAfter(_idleTimeout);
        try
        {
            return await client.SendAsync(request, completion, (idle ?? timeout!).Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Classified as connection_failed by the sync, not as a user cancel.
            throw new TimeoutException(
                $"The WebDAV server did not respond within {_idleTimeout.TotalSeconds:0} seconds.", exception);
        }
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

        // The response body is deliberately not included: it would echo content
        // of whatever the configured URL points at into logs and the UI.
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
                $"WebDAV request failed with status {(int)response.StatusCode}.")
        };
    }
}
