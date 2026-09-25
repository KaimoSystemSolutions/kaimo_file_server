using System.Security.Cryptography;

namespace Kaimo_File_Server.Web.Middleware;

/// <summary>
/// Adds browser security headers to every response. The Content-Security-Policy is
/// defense in depth against script injection: only same-origin script files run, plus
/// inline scripts carrying this request's nonce (<see cref="GetNonce"/>), which the few
/// server-rendered pages (cloud authorization pages) use. Inline styles stay allowed
/// because Blazor components emit <c>style</c> attributes.
/// Framing is limited to the same origin because the file preview embeds same-origin
/// and blob documents in iframes.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    private const string NonceItemKey = "kaimo.csp.nonce";

    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        // Share-link tokens and download tickets travel in URLs; never leak them to other sites.
        headers["Referrer-Policy"] = "no-referrer";
        headers.XFrameOptions = "SAMEORIGIN";
        headers.ContentSecurityPolicy = BuildPolicy(GetNonce(context));

        return next(context);
    }

    /// <summary>The per-request nonce that an inline <c>&lt;script nonce="…"&gt;</c> must carry.</summary>
    public static string GetNonce(HttpContext context)
    {
        if (context.Items.TryGetValue(NonceItemKey, out var existing) && existing is string nonce)
            return nonce;

        nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        context.Items[NonceItemKey] = nonce;
        return nonce;
    }

    internal static string BuildPolicy(string nonce) =>
        "default-src 'self'; " +
        $"script-src 'self' 'nonce-{nonce}'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: blob:; " +
        "media-src 'self' blob:; " +
        "font-src 'self'; " +
        "frame-src 'self' blob:; " +
        "connect-src 'self'; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "frame-ancestors 'self'; " +
        "form-action 'self'";
}
