using System.Net;
using System.Text;
using System.Text.Json;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Infrastructure.ExternalStorage;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class WebDavStorageProviderTests
{
    [Fact]
    public void ParseAndValidate_AcceptsValidHttpsUrl()
    {
        var connection = WebDavConnection("https://cloud.example.com/remote.php/dav/files/user/");

        var settings = WebDavStorageConnectionProvider.ParseAndValidate(connection);

        Assert.Equal("https://cloud.example.com/remote.php/dav/files/user/", settings.ServerUrl);
    }

    [Fact]
    public void ParseAndValidate_AcceptsHttpUrl()
    {
        var connection = WebDavConnection("http://192.168.1.10:8080/webdav");

        var settings = WebDavStorageConnectionProvider.ParseAndValidate(connection);

        Assert.Equal("http://192.168.1.10:8080/webdav", settings.ServerUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://files.example.com/dav")]
    [InlineData("not-a-url")]
    [InlineData("file:///etc/passwd")]
    public void ParseAndValidate_RejectsInvalidUrls(string url)
    {
        var connection = WebDavConnection(url);

        var exception = Assert.Throws<ProtocolConfigurationException>(
            () => WebDavStorageConnectionProvider.ParseAndValidate(connection));

        Assert.Equal("server_url_invalid", exception.Code);
    }

    [Fact]
    public void ParseAndValidate_RejectsUrlExceedingMaxLength()
    {
        string longUrl = "https://example.com/" + new string('a', 2048);
        var connection = WebDavConnection(longUrl);

        var exception = Assert.Throws<ProtocolConfigurationException>(
            () => WebDavStorageConnectionProvider.ParseAndValidate(connection));

        Assert.Equal("server_url_invalid", exception.Code);
    }

    [Fact]
    public void ParseAndValidate_RejectsWrongProviderOrAuthMode()
    {
        var connection = WebDavConnection("https://cloud.example.com/dav/");
        connection.ProviderId = "smb";

        Assert.Throws<ProtocolConfigurationException>(
            () => WebDavStorageConnectionProvider.ParseAndValidate(connection));
    }

    [Fact]
    public void ValidateCredentials_AcceptsValidCredentials()
    {
        var credentials = new Dictionary<string, string>
        {
            ["username"] = "alice",
            ["password"] = "s3cret"
        };

        WebDavStorageConnectionProvider.ValidateCredentials(credentials);
    }

    [Fact]
    public void ValidateCredentials_RejectsMissingUsername()
    {
        var credentials = new Dictionary<string, string> { ["password"] = "s3cret" };

        Assert.Throws<ProtocolConfigurationException>(
            () => WebDavStorageConnectionProvider.ValidateCredentials(credentials));
    }

    [Fact]
    public void ValidateCredentials_RejectsEmptyUsername()
    {
        var credentials = new Dictionary<string, string>
        {
            ["username"] = "   ",
            ["password"] = "s3cret"
        };

        Assert.Throws<ProtocolConfigurationException>(
            () => WebDavStorageConnectionProvider.ValidateCredentials(credentials));
    }

    [Fact]
    public void ValidateCredentials_RejectsMissingPassword()
    {
        var credentials = new Dictionary<string, string> { ["username"] = "alice" };

        Assert.Throws<ProtocolConfigurationException>(
            () => WebDavStorageConnectionProvider.ValidateCredentials(credentials));
    }

    [Fact]
    public void ValidateCredentials_RejectsNewlinesInUsername()
    {
        var credentials = new Dictionary<string, string>
        {
            ["username"] = "alice\r\ninjected",
            ["password"] = "s3cret"
        };

        Assert.Throws<ProtocolConfigurationException>(
            () => WebDavStorageConnectionProvider.ValidateCredentials(credentials));
    }

    [Fact]
    public void ValidateCredentials_RejectsNewlinesInPassword()
    {
        var credentials = new Dictionary<string, string>
        {
            ["username"] = "alice",
            ["password"] = "pass\nword"
        };

        Assert.Throws<ProtocolConfigurationException>(
            () => WebDavStorageConnectionProvider.ValidateCredentials(credentials));
    }

    [Fact]
    public void ValidateCredentials_RejectsOversizedUsername()
    {
        var credentials = new Dictionary<string, string>
        {
            ["username"] = new string('a', 257),
            ["password"] = "s3cret"
        };

        Assert.Throws<ProtocolConfigurationException>(
            () => WebDavStorageConnectionProvider.ValidateCredentials(credentials));
    }

    [Fact]
    public void ValidateCredentials_RejectsOversizedPassword()
    {
        var credentials = new Dictionary<string, string>
        {
            ["username"] = "alice",
            ["password"] = new string('p', 4097)
        };

        Assert.Throws<ProtocolConfigurationException>(
            () => WebDavStorageConnectionProvider.ValidateCredentials(credentials));
    }

    [Fact]
    public void CreateHttpClient_SetsBasicAuthAndAcceptHeader()
    {
        var settings = new WebDavConnectionSettings("https://cloud.example.com/dav/");

        using var client = WebDavStorageConnectionProvider.CreateHttpClient(settings, "alice", "s3cret");

        Assert.NotNull(client.DefaultRequestHeaders.Authorization);
        Assert.Equal("Basic", client.DefaultRequestHeaders.Authorization.Scheme);
        string decoded = Encoding.UTF8.GetString(
            Convert.FromBase64String(client.DefaultRequestHeaders.Authorization.Parameter!));
        Assert.Equal("alice:s3cret", decoded);
        Assert.Contains(client.DefaultRequestHeaders.Accept,
            header => header.MediaType == "application/xml");
        Assert.Equal(TimeSpan.FromSeconds(120), client.Timeout);
    }

    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("/", "/")]
    [InlineData("foo/bar", "/foo/bar")]
    [InlineData("/foo/bar/", "/foo/bar")]
    [InlineData("foo\\bar", "/foo/bar")]
    [InlineData("//a///b//", "/a/b")]
    public void NormalizeStorePath_NormalizesVariousInputs(string? input, string expected)
    {
        Assert.Equal(expected, WebDavPaths.NormalizeStorePath(input));
    }

    [Theory]
    [InlineData("/foo/../bar")]
    [InlineData("/./foo")]
    [InlineData("..")]
    [InlineData("/a/b/..")]
    public void NormalizeStorePath_RejectsTraversalSegments(string input)
    {
        Assert.Throws<ProtocolConfigurationException>(
            () => WebDavPaths.NormalizeStorePath(input));
    }

    [Fact]
    public void NormalizeStorePath_RejectsControlCharacters()
    {
        Assert.Throws<ProtocolConfigurationException>(
            () => WebDavPaths.NormalizeStorePath("/foo\r\nbar"));
        Assert.Throws<ProtocolConfigurationException>(
            () => WebDavPaths.NormalizeStorePath("/foo\0bar"));
    }

    [Fact]
    public void NormalizeStorePath_RejectsOversizedPath()
    {
        string longPath = "/" + new string('a', 4096);
        Assert.Throws<ProtocolConfigurationException>(
            () => WebDavPaths.NormalizeStorePath(longPath));
    }

    [Theory]
    [InlineData("/", "docs", "/docs")]
    [InlineData("/team", "docs", "/team/docs")]
    [InlineData("/a/b", "c", "/a/b/c")]
    public void CombineChild_ProducesCorrectPaths(string parent, string child, string expected)
    {
        Assert.Equal(expected, WebDavPaths.CombineChild(parent, child));
    }

    [Fact]
    public void ParseMultiStatusResponse_ParsesTypicalNextcloudResponse()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <d:multistatus xmlns:d="DAV:">
              <d:response>
                <d:href>/remote.php/dav/files/user/</d:href>
                <d:propstat>
                  <d:status>HTTP/1.1 200 OK</d:status>
                  <d:prop>
                    <d:resourcetype><d:collection/></d:resourcetype>
                    <d:getlastmodified>Wed, 19 Aug 2026 10:00:00 GMT</d:getlastmodified>
                  </d:prop>
                </d:propstat>
              </d:response>
              <d:response>
                <d:href>/remote.php/dav/files/user/Documents/</d:href>
                <d:propstat>
                  <d:status>HTTP/1.1 200 OK</d:status>
                  <d:prop>
                    <d:resourcetype><d:collection/></d:resourcetype>
                    <d:getlastmodified>Thu, 20 Aug 2026 14:30:00 GMT</d:getlastmodified>
                  </d:prop>
                </d:propstat>
              </d:response>
              <d:response>
                <d:href>/remote.php/dav/files/user/report.pdf</d:href>
                <d:propstat>
                  <d:status>HTTP/1.1 200 OK</d:status>
                  <d:prop>
                    <d:resourcetype/>
                    <d:getcontentlength>123456</d:getcontentlength>
                    <d:getlastmodified>Fri, 21 Aug 2026 09:15:00 GMT</d:getlastmodified>
                  </d:prop>
                </d:propstat>
              </d:response>
            </d:multistatus>
            """;

        var store = new WebDavRemoteFileStore(new HttpClient(), "https://cloud.example.com/remote.php/dav/files/user");
        var items = store.ParseMultiStatusResponse(xml, "/");

        Assert.Equal(2, items.Count);

        Assert.Equal("Documents", items[0].Name);
        Assert.Equal("/Documents", items[0].Path);
        Assert.True(items[0].IsDirectory);
        Assert.Null(items[0].Size);

        Assert.Equal("report.pdf", items[1].Name);
        Assert.Equal("/report.pdf", items[1].Path);
        Assert.False(items[1].IsDirectory);
        Assert.Equal(123456L, items[1].Size);
        Assert.True(items[1].ModifiedAtUtc.HasValue);
        Assert.Equal(2026, items[1].ModifiedAtUtc!.Value.Year);
    }

    [Fact]
    public void ParseMultiStatusResponse_SkipsNon200PropStats()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <d:multistatus xmlns:d="DAV:">
              <d:response>
                <d:href>/dav/root/</d:href>
                <d:propstat>
                  <d:status>HTTP/1.1 200 OK</d:status>
                  <d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop>
                </d:propstat>
              </d:response>
              <d:response>
                <d:href>/dav/root/forbidden.txt</d:href>
                <d:propstat>
                  <d:status>HTTP/1.1 403 Forbidden</d:status>
                  <d:prop><d:resourcetype/></d:prop>
                </d:propstat>
              </d:response>
            </d:multistatus>
            """;

        var store = new WebDavRemoteFileStore(new HttpClient(), "https://example.com/dav/root");
        var items = store.ParseMultiStatusResponse(xml, "/");

        Assert.Empty(items);
    }

    [Fact]
    public void ParseMultiStatusResponse_HandlesUrlEncodedHrefs()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <d:multistatus xmlns:d="DAV:">
              <d:response>
                <d:href>/dav/</d:href>
                <d:propstat>
                  <d:status>HTTP/1.1 200 OK</d:status>
                  <d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop>
                </d:propstat>
              </d:response>
              <d:response>
                <d:href>/dav/My%20Documents/</d:href>
                <d:propstat>
                  <d:status>HTTP/1.1 200 OK</d:status>
                  <d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop>
                </d:propstat>
              </d:response>
            </d:multistatus>
            """;

        var store = new WebDavRemoteFileStore(new HttpClient(), "https://example.com/dav");
        var items = store.ParseMultiStatusResponse(xml, "/");

        Assert.Single(items);
        Assert.Equal("My Documents", items[0].Name);
    }

    [Fact]
    public void ParseMultiStatusResponse_ParsesNestedDirectoryListing()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <d:multistatus xmlns:d="DAV:">
              <d:response>
                <d:href>/dav/team/docs/</d:href>
                <d:propstat>
                  <d:status>HTTP/1.1 200 OK</d:status>
                  <d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop>
                </d:propstat>
              </d:response>
              <d:response>
                <d:href>/dav/team/docs/notes.txt</d:href>
                <d:propstat>
                  <d:status>HTTP/1.1 200 OK</d:status>
                  <d:prop>
                    <d:resourcetype/>
                    <d:getcontentlength>42</d:getcontentlength>
                  </d:prop>
                </d:propstat>
              </d:response>
            </d:multistatus>
            """;

        var store = new WebDavRemoteFileStore(new HttpClient(), "https://example.com/dav");
        var items = store.ParseMultiStatusResponse(xml, "/team/docs");

        Assert.Single(items);
        Assert.Equal("notes.txt", items[0].Name);
        Assert.Equal("/team/docs/notes.txt", items[0].Path);
        Assert.Equal(42L, items[0].Size);
    }

    [Fact]
    public async Task EnsureSuccessOrThrow_AcceptsSuccessAndMultiStatus()
    {
        var ok = new HttpResponseMessage(HttpStatusCode.OK);
        var created = new HttpResponseMessage(HttpStatusCode.Created);
        var multiStatus = new HttpResponseMessage(HttpStatusCode.MultiStatus);

        await WebDavRemoteFileStore.EnsureSuccessOrThrowAsync(ok, CancellationToken.None);
        await WebDavRemoteFileStore.EnsureSuccessOrThrowAsync(created, CancellationToken.None);
        await WebDavRemoteFileStore.EnsureSuccessOrThrowAsync(multiStatus, CancellationToken.None);
    }

    [Fact]
    public async Task EnsureSuccessOrThrow_ThrowsAccessDeniedFor401And403()
    {
        var unauthorized = new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("access denied")
        };
        var forbidden = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("forbidden")
        };

        await Assert.ThrowsAsync<RemoteStorageAccessDeniedException>(
            () => WebDavRemoteFileStore.EnsureSuccessOrThrowAsync(unauthorized, CancellationToken.None));
        await Assert.ThrowsAsync<RemoteStorageAccessDeniedException>(
            () => WebDavRemoteFileStore.EnsureSuccessOrThrowAsync(forbidden, CancellationToken.None));
    }

    [Fact]
    public async Task EnsureSuccessOrThrow_ThrowsIOExceptionForNotFound()
    {
        var notFound = new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("not found"),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://example.com/missing.txt")
        };

        var exception = await Assert.ThrowsAsync<IOException>(
            () => WebDavRemoteFileStore.EnsureSuccessOrThrowAsync(notFound, CancellationToken.None));

        Assert.Contains("not found", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnsureSuccessOrThrow_ThrowsIOExceptionForConflict()
    {
        var conflict = new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent("conflict")
        };

        var exception = await Assert.ThrowsAsync<IOException>(
            () => WebDavRemoteFileStore.EnsureSuccessOrThrowAsync(conflict, CancellationToken.None));

        Assert.Contains("409", exception.Message);
    }

    [Fact]
    public async Task EnsureSuccessOrThrow_ThrowsIOExceptionForPreconditionFailed()
    {
        var preconditionFailed = new HttpResponseMessage(HttpStatusCode.PreconditionFailed)
        {
            Content = new StringContent("precondition failed")
        };

        var exception = await Assert.ThrowsAsync<IOException>(
            () => WebDavRemoteFileStore.EnsureSuccessOrThrowAsync(preconditionFailed, CancellationToken.None));

        Assert.Contains("already exists", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnsureSuccessOrThrow_ThrowsIOExceptionForInsufficientStorage()
    {
        var insufficient = new HttpResponseMessage(HttpStatusCode.InsufficientStorage)
        {
            Content = new StringContent("no space")
        };

        var exception = await Assert.ThrowsAsync<IOException>(
            () => WebDavRemoteFileStore.EnsureSuccessOrThrowAsync(insufficient, CancellationToken.None));

        Assert.Contains("507", exception.Message);
    }

    [Fact]
    public async Task EnsureSuccessOrThrow_TruncatesLongErrorBodies()
    {
        var error = new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent(new string('x', 2000))
        };

        var exception = await Assert.ThrowsAsync<IOException>(
            () => WebDavRemoteFileStore.EnsureSuccessOrThrowAsync(error, CancellationToken.None));

        Assert.True(exception.Message.Length < 600);
    }

    [Fact]
    public void ResolveUri_BuildsCorrectUris()
    {
        var store = new WebDavRemoteFileStore(new HttpClient(), "https://cloud.example.com/dav/files/user");

        Assert.Equal(
            new Uri("https://cloud.example.com/dav/files/user/"),
            store.ResolveUri("/", trailingSlash: true));
        Assert.Equal(
            new Uri("https://cloud.example.com/dav/files/user/Documents/"),
            store.ResolveUri("/Documents", trailingSlash: true));
        Assert.Equal(
            new Uri("https://cloud.example.com/dav/files/user/Documents"),
            store.ResolveUri("/Documents", trailingSlash: false));
        Assert.Equal(
            new Uri("https://cloud.example.com/dav/files/user/a/b/c"),
            store.ResolveUri("/a/b/c"));
    }

    [Fact]
    public void ResolveUri_HandlesTrailingSlashOnServerUrl()
    {
        var store = new WebDavRemoteFileStore(new HttpClient(), "https://cloud.example.com/dav/");

        Assert.Equal(
            new Uri("https://cloud.example.com/dav/Documents"),
            store.ResolveUri("/Documents"));
    }

    [Fact]
    public void ResponseOwningStream_DisposesResponseOnDispose()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3])
        };
        var inner = new TrackingStream();
        var owning = new ResponseOwningStream(inner, response);

        owning.Dispose();

        Assert.True(inner.WasDisposed);
    }

    [Fact]
    public async Task ResponseOwningStream_DisposesResponseOnDisposeAsync()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([4, 5, 6])
        };
        var inner = new TrackingStream();
        var owning = new ResponseOwningStream(inner, response);

        await owning.DisposeAsync();

        Assert.True(inner.WasDisposed);
    }

    [Fact]
    public void ResponseOwningStream_DelegatesReadToInnerStream()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3])
        };
        var inner = new MemoryStream([10, 20, 30]);
        var owning = new ResponseOwningStream(inner, response);

        var buffer = new byte[3];
        int read = owning.Read(buffer, 0, 3);

        Assert.Equal(3, read);
        Assert.Equal([10, 20, 30], buffer);
        owning.Dispose();
    }

    [Fact]
    public void WebDavProvider_ExposesCorrectCapabilities()
    {
        var provider = new WebDavStorageConnectionProvider(null!);

        Assert.Equal("webdav", provider.Id);
        Assert.Equal("WebDAV", provider.DisplayName);
        Assert.True(provider.Capabilities.HasFlag(StorageProviderCapabilities.Browse));
        Assert.True(provider.Capabilities.HasFlag(StorageProviderCapabilities.Read));
        Assert.True(provider.Capabilities.HasFlag(StorageProviderCapabilities.Write));
        Assert.True(provider.Capabilities.HasFlag(StorageProviderCapabilities.DirectFileAccess));
        Assert.True(provider.Capabilities.HasFlag(StorageProviderCapabilities.Sync));
        Assert.False(provider.Capabilities.HasFlag(StorageProviderCapabilities.OptimizedSync));
        Assert.False(provider.Capabilities.HasFlag(StorageProviderCapabilities.WatchChanges));
        Assert.False(provider.Capabilities.HasFlag(StorageProviderCapabilities.StableItemIds));
        Assert.Equal([StorageAuthorizationMode.UsernamePassword], provider.AuthorizationModes);
    }

    private static StorageConnection WebDavConnection(string serverUrl) => new()
    {
        ProviderId = "webdav",
        AuthorizationMode = StorageAuthorizationMode.UsernamePassword,
        SettingsJson = JsonSerializer.Serialize(new WebDavConnectionSettings(serverUrl)),
        State = StorageConnectionState.Ready
    };

    private sealed class TrackingStream : MemoryStream
    {
        public bool WasDisposed { get; private set; }
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    }
}
