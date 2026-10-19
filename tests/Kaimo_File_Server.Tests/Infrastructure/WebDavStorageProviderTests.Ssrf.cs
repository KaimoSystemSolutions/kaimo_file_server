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
    // ── SSRF hardening ──

    [Fact]
    public async Task EnsureSuccessOrThrow_DoesNotEchoResponseBody()
    {
        var error = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{\"internal\":\"secret-metadata-token\"}")
        };

        var exception = await Assert.ThrowsAsync<IOException>(
            () => WebDavRemoteFileStore.EnsureSuccessOrThrowAsync(error, CancellationToken.None));

        Assert.DoesNotContain("secret-metadata-token", exception.Message);
        Assert.Contains("500", exception.Message);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.8.9.10", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("::", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("::ffff:169.254.169.254", true)]
    [InlineData("fe80::1", true)]
    [InlineData("224.0.0.1", true)]
    [InlineData("192.168.1.20", false)]   // NAS on the LAN stays reachable
    [InlineData("10.0.0.5", false)]
    [InlineData("172.20.0.3", false)]
    [InlineData("fd00::5", false)]
    [InlineData("203.0.113.10", false)]
    [InlineData("2001:db8::1", false)]
    public void IsBlockedAddress_BlocksOnlyLocalAndNonRoutable(string address, bool blocked)
        => Assert.Equal(blocked, WebDavStorageConnectionProvider.IsBlockedAddress(IPAddress.Parse(address)));

    [Fact]
    public void Handler_DoesNotFollowRedirects()
    {
        using var handler = WebDavStorageConnectionProvider.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.NotNull(handler.ConnectCallback);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    public async Task Client_RefusesToConnectToLoopback(string host)
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = WebDavStorageConnectionProvider.CreateHttpClient(
            new WebDavConnectionSettings($"http://{host}:{port}/"), "alice", "s3cret");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync($"http://{host}:{port}/"));

        // The Basic credentials never left the process.
        Assert.False(listener.Pending());
    }
}
