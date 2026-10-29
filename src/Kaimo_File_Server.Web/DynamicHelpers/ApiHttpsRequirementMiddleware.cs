using Kaimo_File_Server.Web.Controllers.Api;

namespace Kaimo_File_Server.Web.Middleware;

/// <summary>
/// Rejects client-API requests (<c>/api/v1</c>) that did not arrive over HTTPS with
/// <c>426 Upgrade Required</c>. Passwords, refresh tokens and bearer tokens must never cross
/// the network in clear text, and native clients do not follow a redirect for uploads, so the
/// API refuses instead of redirecting. Registered after the forwarded headers, so a request a
/// trusted reverse proxy received over HTTPS (<c>X-Forwarded-Proto: https</c>) is accepted,
/// and before authentication, so a rejected request never costs a password hash or token check.
/// </summary>
public sealed class ApiHttpsRequirementMiddleware(RequestDelegate next)
{
    public const string ErrorCode = "https_required";

    /// <summary>Value of the <c>Upgrade</c> header a 426 response must carry (RFC 9110).</summary>
    public const string UpgradeHeaderValue = "TLS/1.2, HTTP/1.1";

    /// <summary>Path prefix of the client API this requirement applies to.</summary>
    public static readonly PathString PathPrefix = "/api/v1";

    public static readonly ApiError Error =
        new(ErrorCode, "The client API is only available over HTTPS.");

    public Task InvokeAsync(HttpContext context)
    {
        if (context.Request.IsHttps)
            return next(context);

        var response = context.Response;
        response.StatusCode = StatusCodes.Status426UpgradeRequired;
        // No "Connection: Upgrade": it is a connection-specific header that HTTP/2 forbids,
        // and RFC 9110 only requires Upgrade on a 426.
        response.Headers.Upgrade = UpgradeHeaderValue;
        response.Headers.CacheControl = "no-store";
        return response.WriteAsJsonAsync(Error);
    }
}
