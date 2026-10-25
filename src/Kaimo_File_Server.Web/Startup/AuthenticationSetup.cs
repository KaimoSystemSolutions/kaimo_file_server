using System.Security.Claims;
using System.Threading.RateLimiting;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Services;
using Kaimo_File_Server.Web.Controllers.Api;
using Kaimo_File_Server.Web.Controllers.WebDav;
using Kaimo_File_Server.Web.Services;
using Kaimo_File_Server.Web.Services.Api;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Components.Authorization;

namespace Kaimo_File_Server.Web.Startup;

/// <summary>Authentication schemes (web session cookie, device bearer, WebDAV Basic), proxy trust, HTTPS redirection, security monitor, client-API tokens, rate limiting and OpenAPI.</summary>
internal static class AuthenticationSetup
{
    public static void AddKaimoAuthentication(this WebApplicationBuilder builder)
    {
        // -- JWT --
        builder.Services.AddSingleton<JwtTokenService>();
        builder.Services.AddSingleton<WebSessionTicketStore>();
        builder.Services.AddScoped<JwtAuthenticationStateProvider>();
        builder.Services.AddScoped<AuthenticationStateProvider>(sp =>
            sp.GetRequiredService<JwtAuthenticationStateProvider>());

        // -- Authentication. Two JWT schemes share the SAME validation parameters (JwtTokenService)
        //    so they can never drift apart:
        //      • Bearer     — device-scoped tokens in the Authorization header (client API, WebDAV).
        //      • WebSession — the web login token in the HttpOnly session cookie, honored on the
        //                     Blazor hub ONLY (WebSessionCookie.SelectScheme). The API and WebDAV never
        //                     read the cookie, so an ambient browser credential cannot forge requests
        //                     there; BearerTokenValidation additionally rejects any non-device token. --
        // Validated eagerly here (not only in the lazily created JwtTokenService) so the handlers
        // can never run with a weak, well-known or development-only signing secret.
        var apiJwtSecret = JwtTokenService.ValidateSecret(
            builder.Configuration["Jwt:Secret"],
            allowDevelopmentSecret: builder.Environment.IsDevelopment());
        var apiJwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "KaimoFileServer";
        builder.Services
            .AddAuthentication(WebSessionCookie.PolicySchemeName)
            .AddPolicyScheme(WebSessionCookie.PolicySchemeName, null, options =>
                options.ForwardDefaultSelector = WebSessionCookie.SelectScheme)
            .AddJwtBearer(WebSessionCookie.SchemeName, options =>
            {
                options.TokenValidationParameters =
                    JwtTokenService.CreateValidationParameters(apiJwtSecret, apiJwtIssuer);
                options.Events = new JwtBearerEvents
                {
                    // The token comes from the cookie only — never from a header or the query string.
                    OnMessageReceived = context =>
                    {
                        var token = context.Request.Cookies[WebSessionCookie.Name];
                        if (string.IsNullOrEmpty(token))
                            context.NoResult(); // otherwise the handler would fall back to the header
                        else
                            context.Token = token;
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = WebSessionValidation.ValidateAsync,
                    // An invalid session just means an anonymous circuit (login page); no 401 header.
                    OnChallenge = context =>
                    {
                        context.HandleResponse();
                        return Task.CompletedTask;
                    }
                };
            })
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters =
                    JwtTokenService.CreateValidationParameters(apiJwtSecret, apiJwtIssuer);
                // On /dav the WebDAV Basic scheme owns the 401 challenge (Basic realm only —
                // the Windows redirector aborts a mount offered a scheme it cannot satisfy),
                // so the bearer handler must not also add a `WWW-Authenticate: Bearer` header
                // there. Bearer authentication itself still works on /dav.
                options.Events = new JwtBearerEvents
                {
                    // Single choke point for every bearer-authenticated endpoint (/api/v1, /dav):
                    // device-scoped tokens only, active device, enabled account, current security stamp.
                    OnTokenValidated = BearerTokenValidation.ValidateAsync,
                    OnChallenge = context =>
                    {
                        if (context.Request.Path.StartsWithSegments(WebDavPathResolver.Prefix))
                            context.HandleResponse();
                        return Task.CompletedTask;
                    }
                };
            })
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, WebDavBasicAuthenticationHandler>(
                WebDavBasicAuthenticationHandler.SchemeName, _ => { });

        // WebDAV runs in-process as a routed branch of this pipeline (see WebDavController).
        // A five-second-cached view of its settings and an in-memory class-2 lock table.
        builder.Services.AddSingleton<WebDavOptions>();
        builder.Services.AddSingleton<WebDavLockManager>();

        // Honor X-Forwarded-Proto/-For so the HTTPS redirect, HSTS, the session cookie endpoints
        // and `services.webdav.requireHttps` see the real scheme and the login lockout sees the
        // real client behind a reverse proxy. Only peers in ForwardedHeaders:KnownNetworks/
        // KnownProxies are trusted (default: loopback only, see ForwardedHeadersSetup).
        builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(
            options => ForwardedHeadersSetup.Configure(options, builder.Configuration));

        // Browsers are redirected from plain HTTP to HTTPS (the session cookie is Secure-only).
        // The port is the PUBLIC HTTPS port — set HttpsRedirection:HttpsPort when the container's
        // 8443 is published under another port or a reverse proxy terminates TLS on 443.
        builder.Services.AddHttpsRedirection(options =>
            options.HttpsPort = builder.Configuration.GetValue<int?>("HttpsRedirection:HttpsPort")
                                ?? builder.Configuration.GetValue("Kestrel:HttpsPort", 8443));

        // Security overview: records every credential check (web, API, WebDAV) and counts
        // API/WebDAV requests per client, persisted in batches by the flush service.
        // Wraps the login service registered by AddInfrastructure.
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<SecurityMonitor>();
        builder.Services.AddHostedService<SecurityMonitorFlushService>();
        builder.Services.AddScoped<CredentialLoginService>();
        // The service-access check sits inside the monitor, so refused logins are recorded
        // with their real outcome (ServiceDisabled).
        builder.Services.AddScoped<ILoginService>(sp => new MonitoredLoginService(
            new ServiceAccessLoginService(
                sp.GetRequiredService<CredentialLoginService>(),
                sp.GetRequiredService<ServiceAccessGate>(),
                sp.GetRequiredService<IHttpContextAccessor>()),
            sp.GetRequiredService<SecurityMonitor>(),
            sp.GetRequiredService<IHttpContextAccessor>(),
            sp.GetRequiredService<Kaimo_File_Server.Core.Services.Notifications.INotificationPublisher>(),
            sp.GetRequiredService<IUserRepository>()));

        // Issues/rotates client-API access + refresh tokens.
        builder.Services.AddScoped<ApiTokenService>();

        // Per-user throttling for expensive client-API endpoints (currently search, which may
        // walk whole directory trees when Elasticsearch is down). Partitioned by the JWT subject,
        // falling back to the client address (already resolved through trusted proxies only).
        builder.Services.AddRateLimiter(options =>
        {
            options.AddPolicy(SearchApiController.RateLimitPolicy, context =>
                RateLimitPartition.GetTokenBucketLimiter(
                    context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                        ?? context.Connection.RemoteIpAddress?.ToString()
                        ?? "unknown",
                    _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = 10,                              // burst
                        TokensPerPeriod = 1,
                        ReplenishmentPeriod = TimeSpan.FromSeconds(2), // sustained: 30/min
                        QueueLimit = 0
                    }));

            options.OnRejected = async (context, ct) =>
            {
                var response = context.HttpContext.Response;
                response.StatusCode = StatusCodes.Status429TooManyRequests;
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
                await response.WriteAsJsonAsync(
                    new ApiError("rate_limited", "Too many requests. Retry later."), ct);
            };
        });

        // OpenAPI document for per-platform client code generation (served at /openapi/v1.json).
        builder.Services.AddOpenApi("v1");
    }
}
