using Kaimo_File_Server.Core.Logging;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Kaimo_File_Server.Web.Controllers;

[ApiController]
[Route("api/system-logs")]
public sealed class LogDownloadController(
    LogDownloadTokenService tokenService,
    ILogArchiveReader reader) : ControllerBase
{
    [HttpGet("download")]
    public async Task<IActionResult> Download([FromQuery] string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || !tokenService.TryUnprotect(token, out var query))
            return Unauthorized();

        Response.ContentType = "application/x-ndjson; charset=utf-8";
        var datePart = query.UtcDate?.ToString("yyyyMMdd") ?? DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
        Response.Headers[HeaderNames.ContentDisposition] =
            $"attachment; filename=\"kaimo-logs-{datePart}.ndjson\"";
        Response.Headers[HeaderNames.CacheControl] = "no-store";
        Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
        await reader.WriteDownloadAsync(query, Response.Body, cancellationToken);
        return new EmptyResult();
    }
}
