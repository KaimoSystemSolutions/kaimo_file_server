using Kaimo_File_Server.Infrastructure.Backup;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Kaimo_File_Server.Web.Controllers;

[ApiController]
[Route("api/database-backups")]
public sealed class BackupDownloadController(
    BackupDownloadTokenService tokenService,
    IDatabaseBackupService backupService) : ControllerBase
{
    [HttpGet("download")]
    public IActionResult Download([FromQuery] string token)
    {
        if (string.IsNullOrWhiteSpace(token) || !tokenService.TryUnprotect(token, out var fileName))
            return Unauthorized();

        // ResolveBackupPath rejects path traversal and non-backup names and
        // returns null when the file no longer exists.
        var path = backupService.ResolveBackupPath(fileName);
        if (path is null)
            return NotFound();

        Response.Headers[HeaderNames.CacheControl] = "no-store";
        Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";

        var stream = System.IO.File.OpenRead(path);
        return File(stream, "application/octet-stream", fileName);
    }
}
