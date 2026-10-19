using System.Net;
using System.Text;
using System.Text.Json;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Infrastructure.ExternalStorage;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed partial class WebDavStorageProviderTests
{
    [Fact]
    public void ParseAndValidate_AcceptsValidHttpsUrl()
    {
        var connection = WebDavConnection("https://cloud.example.com/remote.php/dav/files/user/");

        var settings = WebDavStorageConnectionProvider.ParseAndValidate(connection);

        Assert.Equal("https://cloud.example.com/remote.php/dav/files/user/", settings.ServerUrl);
    }

    [Fact]
    public void ParseAndValidate_AcceptsHttpUrl_OnlyWithExplicitOptIn()
    {
        var connection = WebDavConnection("http://192.168.1.10:8080/webdav", allowInsecureHttp: true);

        var settings = WebDavStorageConnectionProvider.ParseAndValidate(connection);

        Assert.Equal("http://192.168.1.10:8080/webdav", settings.ServerUrl);
        Assert.True(settings.AllowInsecureHttp);
    }

    /// <summary>Basic credentials must not go over plain HTTP unless the admin opted in.</summary>
    [Fact]
    public void ParseAndValidate_RejectsHttpUrl_WithoutOptIn()
    {
        var connection = WebDavConnection("http://192.168.1.10:8080/webdav");

        var exception = Assert.Throws<ProtocolConfigurationException>(
            () => WebDavStorageConnectionProvider.ParseAndValidate(connection));

        Assert.Equal("server_url_insecure", exception.Code);
    }

    [Fact]
    public void ParseAndValidate_SettingsWithoutOptInProperty_DefaultToHttpsOnly()
    {
        var https = WebDavConnection("https://cloud.example.com/dav/");
        https.SettingsJson = """{"ServerUrl":"https://cloud.example.com/dav/"}""";
        var http = WebDavConnection("http://nas.local/dav/");
        http.SettingsJson = """{"ServerUrl":"http://nas.local/dav/"}""";

        Assert.False(WebDavStorageConnectionProvider.ParseAndValidate(https).AllowInsecureHttp);
        Assert.Equal("server_url_insecure", Assert.Throws<ProtocolConfigurationException>(
            () => WebDavStorageConnectionProvider.ParseAndValidate(http)).Code);
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
        // No whole-request cap (it would abort large uploads); the store applies
        // per-request and idle timeouts instead.
        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
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
    public void ResolveUri_EscapesUrlSyntaxInSegments()
    {
        var store = new WebDavRemoteFileStore(new HttpClient(), "https://cloud.example.com/dav");

        // Unescaped, "dir/a#b" would address "dir/a" (fragment dropped) — a DELETE would hit the wrong item.
        Assert.Equal(
            "https://cloud.example.com/dav/dir/a%23b%3Fc%25d%20e",
            store.ResolveUri("/dir/a#b?c%d e").AbsoluteUri);
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

    private static StorageConnection WebDavConnection(string serverUrl, bool allowInsecureHttp = false) => new()
    {
        ProviderId = "webdav",
        AuthorizationMode = StorageAuthorizationMode.UsernamePassword,
        SettingsJson = JsonSerializer.Serialize(new WebDavConnectionSettings(serverUrl, allowInsecureHttp)),
        State = StorageConnectionState.Ready
    };

    private sealed class TrackingStream : MemoryStream
    {
        public bool WasDisposed { get; private set; }
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    }
}
