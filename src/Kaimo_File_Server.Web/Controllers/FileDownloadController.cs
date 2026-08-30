using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Kaimo_File_Server.Web.Controllers;

/// <summary>
/// Streams a local share file to the browser from a one-time <see cref="FileDownloadTicket"/>.
/// The ticket is issued by the file browser after it has already resolved the signed-in
/// user; access is re-checked here through the ACL-enforcing <see cref="IFileService"/>,
/// so a revoked permission still blocks the download within the ticket's short lifetime.
/// The file is piped straight to the response — never buffered into server memory — which
/// is what lets files larger than the inline-preview cap download without loading a preview.
/// </summary>
[ApiController]
[Route("api/files")]
public sealed class FileDownloadController(
    FileDownloadTicketStore downloadTickets,
    IShareRepository shares,
    IFileServiceFactory fileServiceFactory,
    IUserContextFactory userContextFactory) : ControllerBase
{
    [HttpGet("download")]
    public async Task<IActionResult> Download(string ticket)
    {
        if (string.IsNullOrWhiteSpace(ticket)
            || !downloadTickets.TryConsume(ticket, out var download) || download is null)
            return BadRequest("The download link is invalid or has expired.");

        var share = await shares.GetByIdAsync(download.ShareId);
        var actor = await userContextFactory.CreateByUserIdAsync(download.UserId);
        if (share is null || !share.IsEnabled || actor is null)
            return NotFound();

        if (!ShareRelativePath.TryNormalizeStrict(download.RelativePath, out var relative, allowRoot: false))
            return BadRequest("The download path is invalid.");

        var fs = fileServiceFactory.CreateForShare(share.Id, share.Path);
        try
        {
            var meta = await fs.GetMetadataAsync(relative, actor);
            if (meta.IsDirectory)
                return BadRequest("Cannot download a directory.");

            var content = await fs.ReadFileAsync(relative, actor);
            Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
            Response.Headers[HeaderNames.CacheControl] = "no-store";
            // File(...) streams the response and disposes the stream afterwards.
            return File(content, "application/octet-stream", fileDownloadName: download.FileName);
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return NotFound();
        }
    }
}
