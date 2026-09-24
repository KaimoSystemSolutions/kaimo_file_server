using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Logging;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Kaimo_File_Server.Web.Controllers;

[ApiController]
[Route("api/system-logs")]
public sealed class LogDownloadController(
    LogDownloadTokenService tokenService,
    ILogArchiveReader reader,
    DemoModeOptions demo) : ControllerBase
{
    [HttpGet("download")]
    public async Task<IActionResult> Download(
        [FromQuery] string token,
        [FromQuery] string? format,
        CancellationToken cancellationToken)
    {
        // Logs of a public demo contain other visitors' names and addresses.
        if (demo.ReadOnly)
            return StatusCode(StatusCodes.Status403Forbidden);
        if (string.IsNullOrWhiteSpace(token) || !tokenService.TryUnprotect(token, out var query))
            return Unauthorized();

        var readable = string.Equals(format, "log", StringComparison.OrdinalIgnoreCase);
        Response.ContentType = readable
            ? "text/plain; charset=utf-8"
            : "application/x-ndjson; charset=utf-8";
        var extension = readable ? "log" : "ndjson";
        var datePart = query.UtcDate?.ToString("yyyyMMdd") ?? DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
        Response.Headers[HeaderNames.ContentDisposition] =
            $"attachment; filename=\"kaimo-logs-{datePart}.{extension}\"";
        Response.Headers[HeaderNames.CacheControl] = "no-store";
        Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
        await reader.WriteDownloadAsync(query, Response.Body, readable, cancellationToken);
        return new EmptyResult();
    }
}
