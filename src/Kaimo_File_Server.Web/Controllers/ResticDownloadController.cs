using System.Text;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Backup.Restic;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Kaimo_File_Server.Web.Controllers;

/// <summary>
/// Downloads for the restic file backup. Same model as <see cref="BackupDownloadController"/>:
/// a single-use token issued by an authorized circuit, then a fresh check that the user still
/// exists, is enabled and holds the permission; never available in the read-only demo.
/// </summary>
[ApiController]
[Route("api/file-backups")]
public sealed class ResticDownloadController(
    BackupDownloadTokenService tokenService,
    BackupCatalogService catalog,
    IUserRepository users,
    IUserContextFactory userContexts,
    IManagementAuthService managementAuth,
    DemoModeOptions demo,
    ILogger<ResticDownloadController> logger) : ControllerBase
{
    public const string KitSubjectPrefix = "kit:";

    [HttpGet("kit")]
    public async Task<IActionResult> RecoveryKit([FromQuery] string token, CancellationToken ct)
    {
        // The kit contains the repository password.
        if (demo.ReadOnly)
            return StatusCode(StatusCodes.Status403Forbidden);

        if (string.IsNullOrWhiteSpace(token)
            || !tokenService.TryConsume(token, out var subject, out var userId)
            || !subject.StartsWith(KitSubjectPrefix, StringComparison.Ordinal)
            || !Guid.TryParse(subject[KitSubjectPrefix.Length..], out var repositoryId))
            return Unauthorized();

        var user = await users.GetByIdAsync(userId);
        if (user is null || !user.IsEnabled)
            return Unauthorized();
        var actor = await userContexts.CreateAsync(user);
        if (!await managementAuth.HasGlobalPermissionAsync(actor, ManagementPermission.ManageBackupRepositories))
            return StatusCode(StatusCodes.Status403Forbidden);

        try
        {
            var (fileName, content) = await catalog.BuildRecoveryKitAsync(actor, repositoryId, ct);
            Response.Headers[HeaderNames.CacheControl] = "no-store";
            Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
            return File(Encoding.UTF8.GetBytes(content), "text/plain; charset=utf-8", fileName);
        }
        catch (ResticException ex) when (ex.Code == "repository_missing")
        {
            return NotFound();
        }
        catch (ResticException ex)
        {
            logger.LogWarning("Recovery kit for repository {RepositoryId} could not be built: {Code}.", repositoryId, ex.Code);
            return Problem(statusCode: StatusCodes.Status500InternalServerError, detail: ex.Code);
        }
    }
}
