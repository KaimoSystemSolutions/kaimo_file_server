using System.Globalization;
using Kaimo_File_Server.Core.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Kaimo_File_Server.Web.Controllers.Api;

public partial class BrowseApiController
{
    /// <summary>Lists the stored versions of a file (newest first).</summary>
    [HttpGet("{shareId:guid}/versions")]
    public async Task<IActionResult> Versions(Guid shareId, [FromQuery] string? path)
    {
        var (resolved, error) = await ResolveAsync(shareId, path, allowRoot: false);
        if (error is not null) return error;

        return await GuardAsync(async () =>
        {
            var versions = await resolved!.Fs.GetFileVersionsAsync(resolved.Path, resolved.User);
            var dtos = versions
                .Select(v => new FileVersionDto(v.SnapshotTimestampUtc, v.Size, v.ContentHash))
                .ToList();
            return Ok(dtos);
        });
    }

    /// <summary>Downloads the content of a specific stored version.</summary>
    [HttpGet("{shareId:guid}/version-content")]
    public async Task<IActionResult> VersionContent(
        Guid shareId, [FromQuery] string? path, [FromQuery] string timestamp)
    {
        var (resolved, error) = await ResolveAsync(shareId, path, allowRoot: false);
        if (error is not null) return error;

        if (!DateTime.TryParse(timestamp, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var ts))
            return ApiBadRequest("invalid_timestamp", "timestamp must be an ISO-8601 UTC value.");

        return await GuardAsync(async () =>
        {
            var stream = await resolved!.Fs.ReadFileVersionAsync(resolved.Path, ts, resolved.User);
            Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
            return File(stream, "application/octet-stream",
                fileDownloadName: ShareRelativePath.GetFileName(resolved.Path),
                enableRangeProcessing: true);
        });
    }
}
