using Kaimo_File_Server.Web.Components;
using Kaimo_File_Server.Web.Controllers.WebDav;
using Kaimo_File_Server.Web.Middleware;
using Kaimo_File_Server.Web.Services;

namespace Kaimo_File_Server.Web.Startup;

/// <summary>HTTP middleware pipeline and endpoint mapping. The order is security-relevant; see the comment on each step.</summary>
internal static class PipelineSetup
{
    public static void UseKaimoPipeline(this WebApplication app)
    {
        // First in the pipeline so every response — pages, static assets, API, errors —
        // carries the security headers (CSP, nosniff, referrer and framing policy).
        app.UseMiddleware<SecurityHeadersMiddleware>();

        // Resolve the real client scheme/IP from the reverse proxy before anything reads
        // Request.IsHttps (HSTS, the HTTPS redirect, the session cookie endpoints and the
        // WebDAV Basic handler's HTTPS requirement all depend on it).
        app.UseForwardedHeaders();

        // Browsers on plain HTTP are sent to HTTPS: the session cookie is Secure-only and the
        // password must not cross the network in clear text. The client API and WebDAV are
        // excluded — native clients do not follow redirects for uploads, and WebDAV has its
        // own HTTPS requirement (services.webdav.requireHttps).
        app.UseWhen(
            context => !context.Request.Path.StartsWithSegments("/api/v1")
                       && !context.Request.Path.StartsWithSegments(WebDavPathResolver.Prefix),
            branch => branch.UseHttpsRedirection());

        if (app.Environment.IsDevelopment())
        {
            // Revalidate on every request (cheap 304 via ETag): without a Cache-Control header the
            // browser reuses a stale css/app.css?v=2 heuristically, so style edits seem to have no effect.
            app.UseStaticFiles(new StaticFileOptions
            {
                OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache"
            });
        }
        else
        {
            app.UseExceptionHandler("/Error");
            app.UseHsts();

            var staticWebAssetsManifest = Path.Combine(
                AppContext.BaseDirectory,
                $"{app.Environment.ApplicationName}.staticwebassets.endpoints.json");

            if (File.Exists(staticWebAssetsManifest))
            {
                app.MapStaticAssets();
            }
            else
            {
                // Keep deployments of older/incomplete images operational. Their
                // wwwroot assets can still be served physically even though the
                // MapStaticAssets() manifest is unavailable.
                app.Logger.LogWarning(
                    "Static-web-assets manifest {ManifestPath} is missing; using the physical static-file provider.",
                    staticWebAssetsManifest);
                app.UseStaticFiles();
            }
        }

        // Count API/WebDAV requests per real client address (after the forwarded headers).
        app.UseMiddleware<SecurityMonitorMiddleware>();

        // The Blazor hub authenticates from the session cookie. SameSite=Strict keeps the cookie
        // off cross-site requests, but other ports/subdomains of the same host are "same-site";
        // rejecting foreign Origins closes that gap (cross-site WebSocket hijacking). Placed before
        // authentication so a rejected request never costs a token validation and DB lookups.
        var allowedHubOrigins = WebSessionCookie.ReadAllowedOrigins(app.Configuration);
        app.UseWhen(
            context => context.Request.Path.StartsWithSegments(WebSessionCookie.BlazorHubPath),
            branch => branch.Use(async (context, next) =>
            {
                if (!WebSessionCookie.IsAllowedOrigin(context.Request, allowedHubOrigins))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                await next(context);
            }));

        // Authenticate/authorize before antiforgery and endpoints so [Authorize] API
        // controllers see the JWT-derived principal and the Blazor hub sees the session user.
        app.UseAuthentication();
        app.UseAuthorization();

        // After authorization, where the principal is resolved for every scheme (incl. WebDAV Basic):
        // rejects users an access restriction rule does not allow from this address / host.
        app.UseMiddleware<AccessRestrictionMiddleware>();

        // After authentication, so rate-limit partitions see the JWT subject.
        app.UseRateLimiter();

        // While the WebDAV service is switched off, short-circuit /dav with 503 before the
        // endpoint runs. In-process, so no reconciler — the flag is read with a 5s cache.
        app.UseWhen(
            context => context.Request.Path.StartsWithSegments(WebDavPathResolver.Prefix),
            branch => branch.UseMiddleware<WebDavEnabledMiddleware>());

        app.UseAntiforgery();

        // Serves the client-API OpenAPI document at /openapi/v1.json for client codegen.
        app.MapOpenApi();

        app.MapControllers();
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        app.UseMiddleware<ConfigLocalizationMiddleware>();
    }
}
