using System.Net;
using System.Text.Json;
using Kaimo_File_Server.Web.Middleware;
using Kaimo_File_Server.Web.Services;
using Kaimo_File_Server.Web.Startup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Runs the transport-security segment of the real pipeline
/// (<see cref="PipelineSetup.UseTransportSecurity"/>: forwarded headers, HTTPS redirect,
/// client-API HTTPS requirement) in front of a terminal stand-in for everything that follows
/// it — authentication, rate limiting and the endpoints. The client API must never accept a
/// credential or bearer token over plain HTTP, and the rejection must happen before
/// authentication so a plain-HTTP request never costs a password hash or token validation.
/// </summary>
public class ApiHttpsRequirementTests
{
    [Fact]
    public async Task Login_OverHttp_Returns426BeforeAuthentication()
    {
        var result = await SendAsync("POST", "/api/v1/auth/login", https: false);

        Assert.False(result.ReachedNext);
        Assert.Equal(StatusCodes.Status426UpgradeRequired, result.Context.Response.StatusCode);
        Assert.Equal("TLS/1.2, HTTP/1.1", result.Context.Response.Headers.Upgrade.ToString());
        Assert.Equal("no-store", result.Context.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task Login_OverHttp_ReturnsApiErrorEnvelope()
    {
        var result = await SendAsync("POST", "/api/v1/auth/login", https: false);

        using var body = JsonDocument.Parse(result.Body);
        Assert.Equal(ApiHttpsRequirementMiddleware.ErrorCode, body.RootElement.GetProperty("code").GetString());
        Assert.Equal("https_required", body.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("message").GetString()));
        Assert.StartsWith("application/json", result.Context.Response.ContentType);
    }

    [Theory]
    [InlineData("POST", "/api/v1/auth/refresh")]
    [InlineData("POST", "/api/v1/auth/logout")]
    [InlineData("GET", "/api/v1/browse/shares")]
    [InlineData("GET", "/api/v1/sync/changes")]
    [InlineData("GET", "/API/V1/browse/shares")]
    public async Task ApiRequests_OverHttp_AreRejectedEvenWithBearerToken(string method, string path)
    {
        var result = await SendAsync(method, path, https: false,
            headers: ("Authorization", "Bearer eyJhbGciOiJIUzI1NiJ9.e30.signature"));

        Assert.False(result.ReachedNext);
        Assert.Equal(StatusCodes.Status426UpgradeRequired, result.Context.Response.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/api/v1/auth/login")]
    [InlineData("POST", "/api/v1/auth/refresh")]
    [InlineData("GET", "/api/v1/browse/shares")]
    public async Task ApiRequests_OverHttps_PassThrough(string method, string path)
    {
        var result = await SendAsync(method, path, https: true);

        Assert.True(result.ReachedNext);
        Assert.Equal(StatusCodes.Status200OK, result.Context.Response.StatusCode);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task ForwardedHttps_FromTrustedProxy_PassesThrough(string proxy)
    {
        var result = await SendAsync("POST", "/api/v1/auth/login", https: false, peer: proxy,
            headers: [("X-Forwarded-Proto", "https"), ("X-Forwarded-For", "198.51.100.23")]);

        Assert.True(result.ReachedNext);
        Assert.Equal(StatusCodes.Status200OK, result.Context.Response.StatusCode);
    }

    [Theory]
    [InlineData("172.18.0.5")]
    [InlineData("203.0.113.50")]
    public async Task ForwardedHttps_FromUntrustedPeer_IsRejected(string peer)
    {
        var result = await SendAsync("POST", "/api/v1/auth/login", https: false, peer: peer,
            headers: [("X-Forwarded-Proto", "https"), ("X-Forwarded-For", "198.51.100.23")]);

        Assert.False(result.ReachedNext);
        Assert.Equal(StatusCodes.Status426UpgradeRequired, result.Context.Response.StatusCode);
    }

    [Fact]
    public async Task ForwardedHttp_FromTrustedProxy_IsRejected()
    {
        // A trusted proxy that received the request over plain HTTP says so; the API must follow it.
        var result = await SendAsync("POST", "/api/v1/auth/login", https: true, peer: "127.0.0.1",
            headers: [("X-Forwarded-Proto", "http"), ("X-Forwarded-For", "198.51.100.23")]);

        Assert.False(result.ReachedNext);
        Assert.Equal(StatusCodes.Status426UpgradeRequired, result.Context.Response.StatusCode);
    }

    // ───────────── regressions: everything outside /api/v1 keeps its behavior ─────────────

    [Theory]
    [InlineData("PROPFIND", "/dav")]
    [InlineData("GET", "/dav/share/file.txt")]
    public async Task WebDav_OverHttp_IsNeitherRedirectedNorRejected(string method, string path)
    {
        // WebDAV has its own, configurable HTTPS requirement in the Basic handler.
        var result = await SendAsync(method, path, https: false);

        Assert.True(result.ReachedNext);
        Assert.Equal(StatusCodes.Status200OK, result.Context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/api/public/download")]
    [InlineData("/")]
    [InlineData("/files")]
    [InlineData("/api/v10/anything")]
    public async Task OtherPaths_OverHttp_AreStillRedirectedToHttps(string path)
    {
        var result = await SendAsync("GET", path, https: false);

        Assert.False(result.ReachedNext);
        Assert.Equal(StatusCodes.Status307TemporaryRedirect, result.Context.Response.StatusCode);
        Assert.StartsWith("https://files.example.test:8443" + path, result.Context.Response.Headers.Location.ToString());
    }

    [Theory]
    [InlineData("/api/public/download")]
    [InlineData("/")]
    public async Task OtherPaths_OverHttps_PassThrough(string path)
    {
        var result = await SendAsync("GET", path, https: true);

        Assert.True(result.ReachedNext);
    }

    // ───────────────────────────── harness ─────────────────────────────

    private sealed record PipelineResult(HttpContext Context, bool ReachedNext, string Body);

    private static async Task<PipelineResult> SendAsync(
        string method, string path, bool https, string peer = "192.0.2.10",
        params (string Name, string Value)[] headers)
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(configuration)
            .Configure<ForwardedHeadersOptions>(o => ForwardedHeadersSetup.Configure(o, configuration))
            .AddHttpsRedirection(o => o.HttpsPort = 8443)
            .BuildServiceProvider();

        bool reachedNext = false;
        var app = new ApplicationBuilder(services);
        app.UseTransportSecurity();
        app.Run(context =>
        {
            reachedNext = true;
            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });
        var pipeline = app.Build();

        var context = new DefaultHttpContext { RequestServices = services };
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        context.Request.Method = method;
        context.Request.Scheme = https ? "https" : "http";
        context.Request.Host = new HostString("files.example.test", https ? 8443 : 8081);
        context.Request.Path = path;
        foreach (var (name, value) in headers)
            context.Request.Headers[name] = value;
        var body = new MemoryStream();
        context.Response.Body = body;

        await pipeline(context);

        return new PipelineResult(context, reachedNext, System.Text.Encoding.UTF8.GetString(body.ToArray()));
    }
}
