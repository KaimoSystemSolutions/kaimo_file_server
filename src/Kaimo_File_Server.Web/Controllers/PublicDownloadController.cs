using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Kaimo_File_Server.Web.Controllers;

/// <summary>
/// Streams an anonymous public-share download (single file or ZIP of a selection) from a
/// one-time <see cref="PublicDownloadTicket"/>. The link's policy is re-validated and one
/// access is atomically consumed at stream time; the transfer runs under the link creator's
/// identity through the ACL-enforcing <see cref="IFileService"/> and is confined to the
/// shared item's subtree. An optional per-link bandwidth cap wraps the outgoing stream.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/public")]
public sealed class PublicDownloadController(
    PublicDownloadTicketStore tickets,
    IShareLinkRepository shareLinks,
    IShareRepository shares,
    IFileServiceFactory fileServiceFactory,
    IUserContextFactory userContextFactory) : ControllerBase
{
    [HttpGet("download")]
    public async Task<IActionResult> Download(string ticket, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ticket)
            || !tickets.TryConsume(ticket, out var download) || download is null)
            return BadRequest("The download link is invalid or has expired.");

        // Atomic policy gate: enabled + within window + not exhausted, plus one access consumed.
        var link = await shareLinks.TryConsumeAccessAsync(download.Token);
        if (link is null)
            return NotFound();

        var share = await shares.GetByIdAsync(link.ShareId);
        var actor = await userContextFactory.CreateByUserIdAsync(link.CreatedByUserId);
        // Re-checked per download: a link stops working once its creator is disabled.
        if (share is null || !share.IsEnabled || actor is null || !actor.User.IsEnabled)
            return NotFound();

        // Defense in depth: every requested path must sit within the shared item's subtree.
        var root = ShareRelativePath.Normalize(link.RootRelativePath);
        var requested = new List<string>();
        foreach (var raw in download.RelativePaths)
        {
            if (!ShareRelativePath.TryNormalizeStrict(raw, out var rel, allowRoot: false))
                return BadRequest("The download path is invalid.");
            if (!IsWithin(rel, root))
                return Forbid();
            requested.Add(rel);
        }
        if (requested.Count == 0)
            return BadRequest("Nothing to download.");

        var fs = fileServiceFactory.CreateForShare(share.Id, share.Path);

        Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
        Response.Headers[HeaderNames.CacheControl] = "no-store";

        try
        {
            if (download.Zip)
            {
                // ZipArchive writes synchronously; Kestrel forbids sync IO by default.
                var bodyControl = HttpContext.Features.Get<IHttpBodyControlFeature>();
                if (bodyControl is not null) bodyControl.AllowSynchronousIO = true;

                Response.ContentType = "application/zip";
                Response.Headers[HeaderNames.ContentDisposition] =
                    new ContentDispositionHeaderValue("attachment") { FileNameStar = download.DownloadName }.ToString();

                await using var body = RateLimitedStream.Wrap(Response.Body, link.MaxBytesPerSecond);
                await ShareZipWriter.WriteAsync(body, fs, requested, actor, ct);
                return new EmptyResult();
            }

            var meta = await fs.GetMetadataAsync(requested[0], actor);
            if (meta.IsDirectory)
                return BadRequest("Cannot download a directory as a single file.");

            var content = await fs.ReadFileAsync(requested[0], actor);
            var stream = RateLimitedStream.Wrap(content, link.MaxBytesPerSecond);
            return File(stream, "application/octet-stream", fileDownloadName: download.DownloadName);
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

    private static bool IsWithin(string path, string root)
        => root.Length == 0
           || string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
           || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
}
