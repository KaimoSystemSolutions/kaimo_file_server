using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Backup;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Kaimo_File_Server.Web.Controllers;

[ApiController]
[Route("api/database-backups")]
public sealed class BackupDownloadController(
    BackupDownloadTokenService tokenService,
    IDatabaseBackupService backupService,
    IUserRepository users,
    IUserContextFactory userContexts,
    IManagementAuthService managementAuth,
    DemoModeOptions demo) : ControllerBase
{
    [HttpGet("download")]
    public async Task<IActionResult> Download([FromQuery] string token)
    {
        // A public read-only demo must never hand out a dump of its database.
        if (demo.ReadOnly)
            return StatusCode(StatusCodes.Status403Forbidden);

        if (string.IsNullOrWhiteSpace(token)
            || !tokenService.TryConsume(token, out var fileName, out var userId))
            return Unauthorized();

        // The token was issued by an authorized circuit; re-check that the user
        // still exists, is enabled and still holds the permission right now.
        var user = await users.GetByIdAsync(userId);
        if (user is null || !user.IsEnabled)
            return Unauthorized();
        var actor = await userContexts.CreateAsync(user);
        var permissions = await managementAuth.GetEffectivePermissionsAtAsync(
            actor, ScopeType.Global, Guid.Empty);
        if (!permissions.HasFlag(ManagementPermission.ManageBackups))
            return StatusCode(StatusCodes.Status403Forbidden);

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
