using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;
using Kaimo_File_Server.Core.Services.ExternalStorage;

namespace Kaimo_File_Server.Infrastructure.ExternalStorage;

internal sealed class WebDavRemoteFileStore(
    HttpClient client, string serverUrl, TimeSpan? idleTimeout = null) : IRemoteFileStore
{
    private static readonly XNamespace DavNamespace = "DAV:";
    private static readonly XNamespace MicrosoftNamespace = "urn:schemas-microsoft-com:";

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
            <d:propfind xmlns:d="DAV:" xmlns:z="urn:schemas-microsoft-com:">
              <d:prop>
                <d:resourcetype/>
                <d:getcontentlength/>
                <d:getlastmodified/>
                <d:creationdate/>
                <z:Win32CreationTime/>
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
        => ProppatchTimeAsync(ResolveFileUri(path), "Win32LastModifiedTime", modifiedUtc, cancellationToken);

    /// <summary>
    /// Folder variant: a collection URI ends with a slash (Apache and nginx answer a
    /// slash-less PROPPATCH on a folder with a 301 redirect).
    /// </summary>
    public Task TrySetDirectoryModifiedTimeAsync(
        string path, DateTime modifiedUtc, CancellationToken cancellationToken = default)
        => ProppatchTimeAsync(
            ResolveFileUri(path, trailingSlash: true), "Win32LastModifiedTime", modifiedUtc, cancellationToken);

    public Task TrySetTimesAsync(
        string path, DateTime modifiedUtc, DateTime? createdUtc, CancellationToken cancellationToken = default)
        => ProppatchTimesAsync(ResolveFileUri(path), modifiedUtc, createdUtc, cancellationToken);

    public Task TrySetDirectoryTimesAsync(
        string path, DateTime modifiedUtc, DateTime? createdUtc, CancellationToken cancellationToken = default)
        => ProppatchTimesAsync(ResolveFileUri(path, trailingSlash: true), modifiedUtc, createdUtc, cancellationToken);

    /// <summary>
    /// Both times go in one PROPPATCH, the way the Windows WebDAV redirector sends them
    /// (<c>Win32CreationTime</c>): one round trip, and one feed entry on a Kaimo target. PROPPATCH is
    /// atomic, so a server refusing the creation time also drops the modification time, which the
    /// two-way sync needs; when the response reports any property as not set, the modification time
    /// is sent again on its own and the item keeps the upload time as creation time.
    /// </summary>
    private async Task ProppatchTimesAsync(
        Uri requestUri, DateTime modifiedUtc, DateTime? createdUtc, CancellationToken cancellationToken)
    {
        if (createdUtc is { } created)
        {
            try
            {
                if (await TryProppatchAsync(requestUri,
                        [("Win32LastModifiedTime", modifiedUtc), ("Win32CreationTime", created)], cancellationToken))
                    return;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Retried without the creation time below.
            }
        }
        await ProppatchTimeAsync(requestUri, "Win32LastModifiedTime", modifiedUtc, cancellationToken);
    }

    private async Task ProppatchTimeAsync(
        Uri requestUri, string win32Property, DateTime utc, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendProppatchAsync(
            requestUri, [(win32Property, utc)], cancellationToken);
        await EnsureSuccessOrThrowAsync(response, cancellationToken);
    }

    /// <summary>True only when the server confirms every property; a 207 is checked per property.</summary>
    private async Task<bool> TryProppatchAsync(
        Uri requestUri, IReadOnlyList<(string Win32Property, DateTime Utc)> times, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendProppatchAsync(requestUri, times, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return false;
        return response.StatusCode != HttpStatusCode.MultiStatus
               || AllPropertiesSet(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private async Task<HttpResponseMessage> SendProppatchAsync(
        Uri requestUri, IReadOnlyList<(string Win32Property, DateTime Utc)> times, CancellationToken cancellationToken)
    {
        string props = string.Concat(times.Select(t =>
            $"<z:{t.Win32Property}>{t.Utc.ToUniversalTime():R}</z:{t.Win32Property}>"));
        string body = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <d:propertyupdate xmlns:d="DAV:" xmlns:z="urn:schemas-microsoft-com:">
              <d:set><d:prop>{props}</d:prop></d:set>
            </d:propertyupdate>
            """;
        using var request = new HttpRequestMessage(new HttpMethod("PROPPATCH"), requestUri)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/xml")
        };
        return await SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Whether a PROPPATCH multistatus reports every property with a 2xx status. A response without
    /// per-property statuses, or one that cannot be read, counts as not set.
    /// </summary>
    internal static bool AllPropertiesSet(string multiStatusXml)
    {
        XDocument document;
        try { document = XDocument.Parse(multiStatusXml); }
        catch (System.Xml.XmlException) { return false; }

        var statuses = document.Descendants(DavNamespace + "propstat")
            .Select(propstat => propstat.Element(DavNamespace + "status")?.Value)
            .ToList();
        return statuses.Count > 0 && statuses.All(status =>
            status?.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [_, var code, ..]
            && code.Length == 3 && code[0] == '2');
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

            items.Add(new RemoteStorageItem(name, childPath, isDirectory, size, modified,
                CreatedAtUtc: ParseCreationTime(
                    prop.Element(MicrosoftNamespace + "Win32CreationTime")?.Value,
                    prop.Element(DavNamespace + "creationdate")?.Value,
                    modified)));
        }

        return items;
    }

    /// <summary>
    /// The creation time of a listed item. <c>Win32CreationTime</c> (RFC 1123) wins whenever present:
    /// it is only ever set explicitly (Windows clients, Kaimo's own push and server, IIS), so it is a
    /// real creation time even when later than the modification time (a file copied on Windows).
    /// <c>creationdate</c> (RFC 3339) is used only when strictly earlier than the modification time:
    /// Apache mod_dav and lighttpd report the inode change time there, which is never earlier than the
    /// mtime, so taking it would show the time of the last edit or sync as created.
    /// </summary>
    internal static DateTime? ParseCreationTime(string? win32CreationTime, string? creationDate, DateTime? modifiedUtc)
    {
        if (TryParseUtc(win32CreationTime) is { } win32Created)
            return win32Created;
        if (TryParseUtc(creationDate) is not { } created)
            return null;
        return modifiedUtc is { } modified && created >= modified ? null : created;
    }

    private static DateTime? TryParseUtc(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
               DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed)
            ? parsed.UtcDateTime
            : null;

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
