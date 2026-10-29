using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Logging;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Kaimo_File_Server.Web.Controllers;

[ApiController]
[Route("api/system-logs")]
public sealed class LogDownloadController(
    LogDownloadTokenService tokenService,
    ILogArchiveReader reader,
    IUserRepository users,
    IUserContextFactory userContexts,
    IManagementAuthService managementAuth,
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
        if (string.IsNullOrWhiteSpace(token)
            || !tokenService.TryConsume(token, out var query, out var userId))
            return Unauthorized();

        // The token was issued by an authorized circuit; re-check that the user
        // still exists, is enabled and still holds the permission right now.
        var user = await users.GetByIdAsync(userId);
        if (user is null)
            return Unauthorized();
        if (!user.IsEnabled)
            return StatusCode(StatusCodes.Status403Forbidden);
        var actor = await userContexts.CreateAsync(user);
        if (!await managementAuth.HasGlobalPermissionAsync(actor, ManagementPermission.ViewSystemLogs))
            return StatusCode(StatusCodes.Status403Forbidden);

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
