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

    /// <summary>Subject of a backup-point download: <c>dl:&lt;repository id&gt;:&lt;snapshot id&gt;:&lt;share-relative path&gt;</c>.</summary>
    public const string DownloadSubjectPrefix = "dl:";

    public static string DownloadSubject(Guid repositoryId, string snapshotId, string path)
        => $"{DownloadSubjectPrefix}{repositoryId:N}:{snapshotId}:{path}";

    /// <summary>Streams one file (raw) or folder (ZIP) of a backup point; RestoreFromBackup on its share.</summary>
    [HttpGet("download")]
    public async Task<IActionResult> Download([FromQuery] string token, CancellationToken ct)
    {
        if (demo.ReadOnly)
            return StatusCode(StatusCodes.Status403Forbidden);

        if (string.IsNullOrWhiteSpace(token)
            || !tokenService.TryConsume(token, out var subject, out var userId)
            || !subject.StartsWith(DownloadSubjectPrefix, StringComparison.Ordinal))
            return Unauthorized();
        // The path is last, so it may itself contain ':'.
        var parts = subject[DownloadSubjectPrefix.Length..].Split(':', 3);
        if (parts.Length != 3 || !Guid.TryParseExact(parts[0], "N", out var repositoryId))
            return Unauthorized();

        var user = await users.GetByIdAsync(userId);
        if (user is null || !user.IsEnabled)
            return Unauthorized();
        var actor = await userContexts.CreateAsync(user);

        BackupDownload download;
        try
        {
            download = await catalog.OpenDownloadAsync(actor, repositoryId, parts[1], parts[2], ct);
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }
        catch (ResticException ex) when (ex.Code is "snapshot_missing" or "item_missing")
        {
            return NotFound();
        }
        catch (ResticException ex)
        {
            logger.LogWarning("Backup download from repository {RepositoryId} failed: {Code}.", repositoryId, ex.Code);
            return Problem(statusCode: StatusCodes.Status502BadGateway, detail: ex.Code);
        }

        // Streamed straight from restic: once the first bytes are out, a failure can only abort the response.
        Response.Headers[HeaderNames.CacheControl] = "no-store";
        Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
        Response.ContentType = download.IsDirectory ? "application/zip" : "application/octet-stream";
        Response.Headers[HeaderNames.ContentDisposition] =
            new ContentDispositionHeaderValue("attachment") { FileNameStar = download.FileName }.ToString();
        try
        {
            await download.WriteToAsync(Response.Body, ct);
        }
        catch (Exception ex) when (ex is ResticException or IOException or OperationCanceledException)
        {
            logger.LogWarning("Backup download from repository {RepositoryId} was aborted: {Reason}.", repositoryId,
                ex is ResticException re ? re.Code : ex.GetType().Name);
            HttpContext.Abort();
        }
        return new EmptyResult();
    }

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
