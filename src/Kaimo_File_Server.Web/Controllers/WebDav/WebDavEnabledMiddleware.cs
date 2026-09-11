namespace Kaimo_File_Server.Web.Controllers.WebDav;

/// <summary>
/// Short-circuits every request under <c>/dav</c> with <c>503 Service
/// Unavailable</c> while the WebDAV service is switched off. Because the service
/// is in-process, the enabled flag is read directly (five-second cache) rather
/// than through a reconciler, so toggling it on the settings page takes effect
/// within seconds without a restart.
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

        await _next(context);
    }
}
