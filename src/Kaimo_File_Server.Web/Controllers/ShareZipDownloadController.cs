using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Kaimo_File_Server.Web.Controllers;

/// <summary>
/// Streams a selection of share items (files and/or folders) to the browser as a ZIP from a
/// one-time <see cref="ZipDownloadTicket"/>. The ticket is issued by the file browser after it
/// resolved the signed-in user; access is re-checked here per entry through the ACL-enforcing
/// <see cref="IFileService"/>. The archive is written straight to the response — never staged.
/// </summary>
[ApiController]
[Route("api/files")]
public sealed class ShareZipDownloadController(
    ZipDownloadTicketStore zipTickets,
    IShareRepository shares,
    IFileServiceFactory fileServiceFactory,
    IUserContextFactory userContextFactory) : ControllerBase
{
    [HttpGet("download-zip")]
    public async Task<IActionResult> DownloadZip(string ticket, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ticket)
            || !zipTickets.TryConsume(ticket, out var download) || download is null)
            return BadRequest("The download link is invalid or has expired.");

        var share = await shares.GetByIdAsync(download.ShareId);
        var actor = await userContextFactory.CreateByUserIdAsync(download.UserId);
        if (share is null || !share.IsEnabled || actor is null)
            return NotFound();

        var fs = fileServiceFactory.CreateForShare(share.Id, share.Path);

        // ZipArchive writes its central directory synchronously; Kestrel forbids sync IO by
        // default. Allow it for this response so the archive can stream without buffering to disk.
        var bodyControl = HttpContext.Features.Get<IHttpBodyControlFeature>();
        if (bodyControl is not null) bodyControl.AllowSynchronousIO = true;

        Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
        Response.Headers[HeaderNames.CacheControl] = "no-store";
        Response.ContentType = "application/zip";
        Response.Headers[HeaderNames.ContentDisposition] =
            new ContentDispositionHeaderValue("attachment") { FileNameStar = download.ArchiveName }.ToString();

        await ShareZipWriter.WriteAsync(Response.Body, fs, download.RelativePaths, actor, ct);
        return new EmptyResult();
    }
}
