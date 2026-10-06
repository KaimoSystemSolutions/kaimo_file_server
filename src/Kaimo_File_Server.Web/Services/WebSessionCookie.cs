using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// The browser session of the Blazor web UI: the login JWT lives in an <c>HttpOnly</c>,
/// <c>Secure</c>, <c>SameSite=Strict</c> cookie, so script on the page (including an XSS
/// payload) can never read it.
///
/// The cookie is accepted on exactly one path — the Blazor hub (<see cref="BlazorHubPath"/>),
/// where the circuit's user is established. The client API (<c>/api/v1</c>) and WebDAV
/// (<c>/dav</c>) never read it, so the ambient cookie adds no CSRF surface there.
/// </summary>
public static class WebSessionCookie
{
    /// <summary>The <c>__Host-</c> prefix forces Secure, Path=/ and no Domain attribute.</summary>
    public const string Name = "__Host-kaimo_session";

    /// <summary>Authentication scheme that reads the JWT from the cookie.</summary>
    public const string SchemeName = "WebSession";

    /// <summary>Default policy scheme that forwards to <see cref="SchemeName"/> or the bearer scheme.</summary>
    public const string PolicySchemeName = "Kaimo";

    /// <summary>Path of the Blazor Server hub (negotiate + WebSocket).</summary>
    public const string BlazorHubPath = "/_blazor";

    /// <summary>Configuration key listing extra origins allowed to open the Blazor hub
    /// (e.g. the public URL when a reverse proxy rewrites the Host header).</summary>
    public const string AllowedOriginsKey = "Web:AllowedOrigins";

    public static void Append(HttpResponse response, string token, DateTimeOffset expires)
        => response.Cookies.Append(Name, token, CreateOptions(expires));

    public static void Delete(HttpResponse response)
        => response.Cookies.Delete(Name, CreateOptions(expires: null));

    internal static CookieOptions CreateOptions(DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        IsEssential = true,
        Expires = expires,
    };

    /// <summary>
    /// Forward selector of the default authentication scheme: the session cookie is honored
    /// on the Blazor hub only; every other path keeps the device-scoped bearer scheme.
    /// </summary>
    public static string SelectScheme(HttpContext context)
        => context.Request.Path.StartsWithSegments(BlazorHubPath)
            ? SchemeName
            : JwtBearerDefaults.AuthenticationScheme;

    /// <summary>Extra allowed origins from <see cref="AllowedOriginsKey"/> (array or comma-separated).</summary>
    public static string[] ReadAllowedOrigins(IConfiguration configuration)
    {
        var section = configuration.GetSection(AllowedOriginsKey);
        var values = section.Get<string[]>() ?? section.Value?.Split(',') ?? [];
        return values.Select(v => v.Trim().TrimEnd('/')).Where(v => v.Length > 0).ToArray();
    }

    /// <summary>
    /// Whether a present <c>Origin</c> header names this server. Requests without the header
    /// pass (non-browser clients, old browsers); cross-origin browser requests do not.
    /// Guards the Blazor hub against cross-site WebSocket hijacking from sibling sites
    /// (other ports/subdomains count as same-site, so SameSite alone does not stop them).
    /// </summary>
    public static bool IsAllowedOrigin(HttpRequest request, IReadOnlyCollection<string> extraOrigins)
    {
        string? origin = request.Headers.Origin;
        return string.IsNullOrEmpty(origin) || IsOwnOrigin(request, origin, extraOrigins);
    }

    /// <summary>
    /// Strict check for the cookie-setting endpoints: HTTPS, an <c>Origin</c> header naming this
    /// server (browsers always send it on POST) and, when present, <c>Sec-Fetch-Site: same-origin</c>.
    /// Blocks login CSRF (planting a session) and cross-site sign-out.
    /// </summary>
    public static bool IsSameOriginFetch(HttpRequest request, IReadOnlyCollection<string> extraOrigins)
    {
        if (!request.IsHttps)
            return false;

        string? origin = request.Headers.Origin;
        if (string.IsNullOrEmpty(origin) || !IsOwnOrigin(request, origin, extraOrigins))
            return false;

        string? site = request.Headers["Sec-Fetch-Site"];
        return string.IsNullOrEmpty(site) || site == "same-origin";
    }

    private static bool IsOwnOrigin(HttpRequest request, string origin, IReadOnlyCollection<string> extraOrigins)
    {
        origin = origin.TrimEnd('/');
        return string.Equals(origin, $"{request.Scheme}://{request.Host.Value}", StringComparison.OrdinalIgnoreCase)
               || extraOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase);
    }
}
