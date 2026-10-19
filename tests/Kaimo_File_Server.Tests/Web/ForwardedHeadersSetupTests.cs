using System.Net;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Runs the real <see cref="ForwardedHeadersMiddleware"/> with the options from
/// <see cref="ForwardedHeadersSetup"/>: a spoofed <c>X-Forwarded-For</c> from an
/// untrusted peer must not change the client address the login lockout keys on.
/// </summary>
public class ForwardedHeadersSetupTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task DefaultTrust_HonorsLoopbackProxy(string peer)
    {
        var context = await RunAsync(Config(), peer, "198.51.100.23", "https");

        Assert.Equal(IPAddress.Parse("198.51.100.23"), context.Connection.RemoteIpAddress);
        Assert.True(context.Request.IsHttps);
    }

    /// <summary>
    /// Any LAN machine (or the Docker bridge gateway of the userland proxy) is a private
    /// address; by default it must not be able to pick its own client address per request,
    /// otherwise the per-(user, address) login lockout is trivially bypassed.
    /// </summary>
    [Theory]
    [InlineData("172.18.0.5")]
    [InlineData("::ffff:172.18.0.5")]
    [InlineData("192.168.1.10")]
    [InlineData("10.0.0.7")]
    public async Task DefaultTrust_IgnoresHeadersFromPrivatePeer(string peer)
    {
        var context = await RunAsync(Config(), peer, "198.51.100.23", "https");

        Assert.NotEqual(IPAddress.Parse("198.51.100.23"), context.Connection.RemoteIpAddress);
        Assert.False(context.Request.IsHttps);
    }

    [Fact]
    public async Task ConfiguredDockerNetwork_HonorsProxyInThatNetwork()
    {
        var config = Config(("ForwardedHeaders:KnownNetworks:0", "172.18.0.0/16"));

        var context = await RunAsync(config, "172.18.0.5", "198.51.100.23", "https");

        Assert.Equal(IPAddress.Parse("198.51.100.23"), context.Connection.RemoteIpAddress);
        Assert.True(context.Request.IsHttps);
    }

    [Fact]
    public async Task DefaultTrust_IgnoresHeadersFromPublicPeer()
    {
        var context = await RunAsync(Config(), "203.0.113.50", "10.9.9.9", "https");

        Assert.Equal(IPAddress.Parse("203.0.113.50"), context.Connection.RemoteIpAddress);
        Assert.False(context.Request.IsHttps);
    }

    [Fact]
    public async Task OnlyLastHopIsUsed()
    {
        // The client prepends a fake address; the proxy appends the real one.
        var context = await RunAsync(Config(), "127.0.0.1", "1.1.1.1, 198.51.100.23", "https");

        Assert.Equal(IPAddress.Parse("198.51.100.23"), context.Connection.RemoteIpAddress);
    }

    [Fact]
    public async Task ConfiguredProxy_ReplacesDefaultRanges()
    {
        var config = Config(("ForwardedHeaders:KnownProxies:0", "10.0.0.2"));

        var trusted = await RunAsync(config, "10.0.0.2", "198.51.100.23", "https");
        var other = await RunAsync(config, "172.18.0.5", "198.51.100.23", "https");

        Assert.Equal(IPAddress.Parse("198.51.100.23"), trusted.Connection.RemoteIpAddress);
        Assert.Equal(IPAddress.Parse("172.18.0.5"), other.Connection.RemoteIpAddress);
    }

    [Fact]
    public async Task ConfiguredNetwork_IsTrusted()
    {
        var config = Config(("ForwardedHeaders:KnownNetworks:0", "203.0.113.0/24"));

        var context = await RunAsync(config, "203.0.113.50", "198.51.100.23", "https");

        Assert.Equal(IPAddress.Parse("198.51.100.23"), context.Connection.RemoteIpAddress);
    }

    private static IConfiguration Config(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static async Task<HttpContext> RunAsync(
        IConfiguration configuration, string peer, string forwardedFor, string forwardedProto)
    {
        var options = new ForwardedHeadersOptions();
        ForwardedHeadersSetup.Configure(options, configuration);
        var middleware = new ForwardedHeadersMiddleware(
            _ => Task.CompletedTask, NullLoggerFactory.Instance, Options.Create(options));

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        context.Request.Scheme = "http";
        context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        context.Request.Headers["X-Forwarded-Proto"] = forwardedProto;
        await middleware.Invoke(context);
        return context;
    }
}
