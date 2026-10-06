namespace Kaimo_File_Server.Web.Controllers.WebDav;

/// <summary>
/// Short-circuits every request under <c>/dav</c> with <c>503 Service
/// Unavailable</c> while the WebDAV service is switched off. Because the service
/// is in-process, the enabled flag is read directly (five-second cache) rather
/// than through a reconciler, so toggling it on the settings page takes effect
/// within seconds without a restart.
///
/// It also rejects cross-site browser requests: a browser that once answered the Basic
/// prompt for <c>/dav</c> re-sends those credentials automatically, even on requests
/// triggered by another site. Native WebDAV clients send no <c>Sec-Fetch-Site</c> header
/// and are unaffected.
/// </summary>
public sealed class WebDavEnabledMiddleware
{
    private readonly RequestDelegate _next;
    private readonly WebDavOptions _options;

    public WebDavEnabledMiddleware(RequestDelegate next, WebDavOptions options)
    {
        _next = next;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var snapshot = await _options.GetAsync();
        if (!snapshot.Enabled)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        if (IsCrossSiteBrowserRequest(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await _next(context);
    }

    /// <summary>A browser request initiated by another origin (typed URLs and bookmarks send "none").</summary>
    internal static bool IsCrossSiteBrowserRequest(HttpRequest request)
    {
        string? site = request.Headers["Sec-Fetch-Site"];
        return !string.IsNullOrEmpty(site) && site != "same-origin" && site != "none";
    }
}
