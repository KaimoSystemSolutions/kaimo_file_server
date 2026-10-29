using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Kaimo_File_Server.Web.Controllers.Api;
using Kaimo_File_Server.Web.Startup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// The fixed request limit on the client-API authentication endpoints. It caps login,
/// refresh and logout requests per client address independently of the credential lockout,
/// so a flood of requests is turned away before it reaches the login service. Runs the real
/// rate-limiter configuration (<see cref="AuthenticationSetup.ConfigureRateLimiter"/>) behind
/// routing, with stand-in endpoints that carry the controller's policy.
/// </summary>
public class AuthRateLimitTests
{
    private const string ClientA = "198.51.100.10";
    private const string ClientB = "198.51.100.11";

    [Fact]
    public void AuthController_UsesTheAuthPolicy()
    {
        var attribute = typeof(AuthApiController).GetCustomAttribute<EnableRateLimitingAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(AuthApiController.RateLimitPolicy, attribute!.PolicyName);
        Assert.Equal(20, AuthApiController.RequestsPerMinute);
    }

    [Fact]
    public async Task TwentyRequestsPerMinute_AreAllowed_TheTwentyFirstIsRejected()
    {
        var pipeline = BuildPipeline();

        for (int i = 0; i < AuthApiController.RequestsPerMinute; i++)
            Assert.Equal(StatusCodes.Status200OK, (await SendAsync(pipeline, "/api/v1/auth/login", ClientA)).Status);

        var rejected = await SendAsync(pipeline, "/api/v1/auth/login", ClientA);

        Assert.Equal(StatusCodes.Status429TooManyRequests, rejected.Status);
        Assert.True(int.TryParse(rejected.RetryAfter, out var seconds) && seconds is > 0 and <= 60,
            $"Retry-After was '{rejected.RetryAfter}'");
        using var body = JsonDocument.Parse(rejected.Body);
        Assert.Equal("rate_limited", body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task LoginRefreshAndLogout_ShareOneBudgetPerAddress()
    {
        var pipeline = BuildPipeline();

        for (int i = 0; i < 10; i++)
            await SendAsync(pipeline, "/api/v1/auth/login", ClientA);
        for (int i = 0; i < 10; i++)
            await SendAsync(pipeline, "/api/v1/auth/refresh", ClientA);

        Assert.Equal(StatusCodes.Status429TooManyRequests,
            (await SendAsync(pipeline, "/api/v1/auth/logout", ClientA)).Status);
    }

    [Fact]
    public async Task OtherAddresses_KeepTheirOwnBudget()
    {
        var pipeline = BuildPipeline();

        for (int i = 0; i <= AuthApiController.RequestsPerMinute; i++)
            await SendAsync(pipeline, "/api/v1/auth/login", ClientA);

        Assert.Equal(StatusCodes.Status200OK, (await SendAsync(pipeline, "/api/v1/auth/login", ClientB)).Status);
    }

    [Fact]
    public async Task EndpointsWithoutThePolicy_AreNotLimited()
    {
        var pipeline = BuildPipeline();

        for (int i = 0; i <= AuthApiController.RequestsPerMinute; i++)
            await SendAsync(pipeline, "/api/v1/auth/login", ClientA);

        Assert.Equal(StatusCodes.Status200OK, (await SendAsync(pipeline, "/api/v1/browse/shares", ClientA)).Status);
    }

    // ───────────────────────────── harness ─────────────────────────────

    private sealed record Response(int Status, string? RetryAfter, string Body);

    private static RequestDelegate BuildPipeline()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(new DiagnosticListener("AuthRateLimitTests"))
            .AddRouting()
            .AddRateLimiter(AuthenticationSetup.ConfigureRateLimiter)
            .BuildServiceProvider();

        var app = new ApplicationBuilder(services);
        app.Use((context, next) =>
        {
            context.RequestServices = services;
            return next(context);
        });
        app.UseRouting();
        app.UseRateLimiter();
        app.UseEndpoints(endpoints =>
        {
            RequestDelegate ok = context =>
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            };
            foreach (var action in new[] { "login", "refresh", "logout" })
                endpoints.MapPost($"/api/v1/auth/{action}", ok).RequireRateLimiting(AuthApiController.RateLimitPolicy);
            endpoints.MapPost("/api/v1/browse/shares", ok);
        });
        return app.Build();
    }

    private static async Task<Response> SendAsync(RequestDelegate pipeline, string path, string client)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(client);
        context.Request.Method = "POST";
        context.Request.Scheme = "https";
        context.Request.Path = path;
        var body = new MemoryStream();
        context.Response.Body = body;

        await pipeline(context);

        return new Response(
            context.Response.StatusCode,
            context.Response.Headers.RetryAfter.ToString(),
            System.Text.Encoding.UTF8.GetString(body.ToArray()));
    }
}
