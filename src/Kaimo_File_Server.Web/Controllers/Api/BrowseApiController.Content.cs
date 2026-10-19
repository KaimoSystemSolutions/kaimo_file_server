using System.Globalization;
using Kaimo_File_Server.Core.Services.File;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Kaimo_File_Server.Web.Controllers.Api;

public partial class BrowseApiController
{
    /// <summary>
    /// Downloads a file's content. Supports HTTP Range for resumable transfers. With
    /// <c>X-Kaimo-Preserve-Access-Time: true</c> (sync clients) the read does not count as an access.
    /// </summary>
    [HttpGet("{shareId:guid}/content")]
    public async Task<IActionResult> Download(Guid shareId, [FromQuery] string? path)
    {
        var (resolved, error) = await ResolveAsync(shareId, path);
        if (error is not null) return error;

        return await GuardAsync(async () =>
        {
            var meta = await resolved!.Fs.GetMetadataAsync(resolved.Path, resolved.User);
            if (meta.IsDirectory)
                return ApiBadRequest("is_directory", "Cannot download a directory.");

            var preserveAccessTime = bool.TryParse(
                Request.Headers[PreserveAccessTimeHeader].ToString(), out var preserve) && preserve;
            var stream = await resolved.Fs.ReadFileAsync(resolved.Path, resolved.User, preserveAccessTime);
            Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
            Response.Headers[HeaderNames.ETag] = ItemTag.For(meta);
            Response.Headers[HeaderNames.LastModified] =
                meta.ModifiedAt.ToUniversalTime().ToString("R");
            Response.Headers[CreatedAtHeader] = new DateTimeOffset(meta.CreatedAt.ToUniversalTime())
                .ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            // enableRangeProcessing lets clients resume/segment large downloads.
            return File(stream, "application/octet-stream",
                fileDownloadName: meta.Name,
                enableRangeProcessing: true);
        });
    }

    /// <summary>
    /// Uploads (creates or overwrites) a file from the request body. Honors
    /// <c>If-Match</c> (overwrite only when the current item still matches — a
    /// mismatch or a missing file yields 412) and <c>If-None-Match: *</c>
    /// (create-only — fail if the file already exists). Supports <c>Idempotency-Key</c>
    /// so a retried upload replays its stored result instead of re-writing. An optional
    /// <c>X-Kaimo-Modified-At</c> header (Unix epoch milliseconds, UTC) preserves the
    /// source modification time — on an overwrite only when it is newer than the stored
    /// one; otherwise, and without the header, the file keeps its write time. A stamped time
    /// is also used as the last-access time (the copy was not opened since). An optional
    /// <c>X-Kaimo-Created-At</c> header sets the creation time of a new file; an overwrite adopts
    /// it only when it is older, and otherwise keeps the creation time of the replaced file.
    /// </summary>
    [HttpPut("{shareId:guid}/content")]
    public async Task<IActionResult> Upload(Guid shareId, [FromQuery] string? path, CancellationToken ct)
    {
        var (resolved, error) = await ResolveAsync(shareId, path);
        if (error is not null) return error;

        if (string.IsNullOrEmpty(resolved!.Path))
            return ApiBadRequest("invalid_path", "A file path is required.");
        var target = SanitizeTarget(resolved.Fs, resolved.Path);

        // The body is the raw file content. A form content-type makes MVC's form value provider
        // read and drain Request.Body during model binding, so WriteFileAsync would persist an
        // EMPTY file and still return 200 — a silent, destructive overwrite. Reject it outright;
        // a raw upload is never a form.
        if (Request.HasFormContentType)
            return ApiBadRequest("unsupported_content_type",
                "Upload body must be raw file bytes, not a form "
                + "(Content-Type must not be multipart/form-data or application/x-www-form-urlencoded).");

        if (!TryReadTimeHeader(ModifiedAtHeader, out var modifiedAtUtc, out var rawModifiedAt))
            return InvalidModifiedAt();
        if (!TryReadTimeHeader(CreatedAtHeader, out var createdAtUtc, out var rawCreatedAt))
            return InvalidCreatedAt();

        // The times are part of the fingerprint (reusing a key with a different time is a
        // different request, not a replay); without them the fingerprint stays as before.
        var fingerprint = RequestFingerprint.Compute(
            "PUT", $"{shareId}/{resolved.Path}",
            "len:" + (Request.ContentLength?.ToString() ?? "?")
            + (modifiedAtUtc is null ? "" : ";mtime:" + rawModifiedAt.Trim())
            + (createdAtUtc is null ? "" : ";ctime:" + rawCreatedAt.Trim()));

        return await ExecuteIdempotentAsync(resolved.User.User.Id, fingerprint, () => GuardAsync(async () =>
        {
            var ifMatch = Request.Headers[HeaderNames.IfMatch].ToString();
            var createOnly = IsStar(Request.Headers[HeaderNames.IfNoneMatch].ToString());

            if (createOnly || !string.IsNullOrWhiteSpace(ifMatch))
            {
                var current = await TryGetMetadataAsync(resolved.Fs, target, resolved.User);
                if (createOnly && current is not null)
                    return ApiPreconditionFailed("The file already exists.");
                if (!string.IsNullOrWhiteSpace(ifMatch) && (current is null || !ItemTag.Matches(ifMatch, current)))
                    return ApiPreconditionFailed();
            }

            await resolved.Fs.WriteFileAsync(target, Request.Body, resolved.User, modifiedAtUtc, createdAtUtc, ct);
            var meta = await resolved.Fs.GetMetadataAsync(target, resolved.User);
            Response.Headers[HeaderNames.ETag] = ItemTag.For(meta);
            return Ok(FileEntryDto.From(meta));
        }));
    }

    /// <summary>
    /// Stamps the modification time (and, like an upload, the last-access time) of an existing
    /// file or directory from the <c>X-Kaimo-Modified-At</c> header and/or its creation time from
    /// <c>X-Kaimo-Created-At</c>; at least one is required. Sync clients use it to give a folder the
    /// source times after writing its content. Retries are safe: the same time yields the same item
    /// tag, though each call appends another <c>Modified</c> feed entry.
    /// </summary>
    [HttpPut("{shareId:guid}/modified-at")]
    public async Task<IActionResult> SetModifiedAt(Guid shareId, [FromQuery] string? path)
    {
        var (resolved, error) = await ResolveAsync(shareId, path);
        if (error is not null) return error;

        if (string.IsNullOrEmpty(resolved!.Path))
            return ApiBadRequest("invalid_path", "A file or directory path is required.");

        if (!TryReadTimeHeader(ModifiedAtHeader, out var modifiedAtUtc, out _))
            return InvalidModifiedAt();
        if (!TryReadTimeHeader(CreatedAtHeader, out var createdAtUtc, out _))
            return InvalidCreatedAt();
        if (modifiedAtUtc is null && createdAtUtc is null)
            return ApiBadRequest("missing_time_header", $"{ModifiedAtHeader} or {CreatedAtHeader} is required.");

        return await GuardAsync(async () =>
        {
            await resolved.Fs.SetModifiedAtAsync(resolved.Path, resolved.User, modifiedAtUtc, createdAtUtc);
            var meta = await resolved.Fs.GetMetadataAsync(resolved.Path, resolved.User);
            Response.Headers[HeaderNames.ETag] = ItemTag.For(meta);
            return Ok(FileEntryDto.From(meta));
        });
    }

    /// <summary>
    /// Renames the new segments of a write target that Windows cannot use ("a:b" → "a_b";
    /// existing entries keep their name) and reports the final path in <see cref="FinalPathHeader"/>.
    /// The idempotency fingerprint stays on the requested path; the mapping is deterministic.
    /// </summary>
    private string SanitizeTarget(IFileService fs, string path)
    {
        var target = WindowsFileNameHelper.SanitizeNewSegments(path, p => Path.Exists(fs.ToAbsolutePath(p)));
        if (target != path)
            Response.Headers[FinalPathHeader] = target;
        return target;
    }

    /// <summary>
    /// Parses an optional epoch-milliseconds time header (<c>X-Kaimo-Modified-At</c>,
    /// <c>X-Kaimo-Created-At</c>). Returns false only for a present but invalid value; an absent
    /// header yields true with a null time.
    /// </summary>
    private bool TryReadTimeHeader(string header, out DateTime? utc, out string raw)
    {
        utc = null;
        raw = Request.Headers[header].ToString();
        if (string.IsNullOrWhiteSpace(raw))
            return true;

        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epochMs)
            || epochMs < 0 || epochMs > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
            return false;

        utc = DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime;
        return true;
    }

    private IActionResult InvalidModifiedAt()
        => ApiBadRequest("invalid_modified_at", $"{ModifiedAtHeader} must be Unix epoch milliseconds (UTC).");

    private IActionResult InvalidCreatedAt()
        => ApiBadRequest("invalid_created_at", $"{CreatedAtHeader} must be Unix epoch milliseconds (UTC).");
}
