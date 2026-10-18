using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Infrastructure.Services;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Controllers.WebDav;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Kaimo_File_Server.Web.Controllers.Api;

/// <summary>
/// Live file-browser transport for client apps. Every operation delegates to the
/// ACL-checked <see cref="IFileService"/>, so a caller only ever sees or touches
/// what their permissions allow. Untrusted paths are validated through
/// <see cref="ShareRelativePath"/> before use.
/// </summary>
[Authorize]
[Route("api/v1/browse")]
public sealed class BrowseApiController : ApiControllerBase
{
    /// <summary>Optional upload header carrying the source mtime as Unix epoch milliseconds (UTC).</summary>
    public const string ModifiedAtHeader = "X-Kaimo-Modified-At";

    /// <summary>
    /// Optional upload / stamp header carrying the source creation time as Unix epoch milliseconds
    /// (UTC); also returned on downloads so a client can give its copy the same creation time.
    /// </summary>
    public const string CreatedAtHeader = "X-Kaimo-Created-At";

    /// <summary>
    /// Optional download header (<c>true</c>): the read is a sync transfer, not a user access, so the
    /// file's last-access time is left unchanged.
    /// </summary>
    public const string PreserveAccessTimeHeader = "X-Kaimo-Preserve-Access-Time";

    /// <summary>
    /// Response header with the path actually written when the requested target had to be
    /// renamed to a Windows-valid name ("a:b.txt" → "a_b.txt"). Absent when nothing was renamed.
    /// </summary>
    public const string FinalPathHeader = "X-Kaimo-Path";

    private readonly IUserContextFactory _userContextFactory;
    private readonly IShareRepository _shares;
    private readonly IFileServiceFactory _fileServiceFactory;
    private readonly HomeDirectoryService _homes;
    private readonly ILogger<BrowseApiController> _logger;

    public BrowseApiController(
        IUserContextFactory userContextFactory,
        IShareRepository shares,
        IFileServiceFactory fileServiceFactory,
        HomeDirectoryService homes,
        ILogger<BrowseApiController> logger)
    {
        _userContextFactory = userContextFactory;
        _shares = shares;
        _fileServiceFactory = fileServiceFactory;
        _homes = homes;
        _logger = logger;
    }

    /// <summary>Lists the shares the caller may browse.</summary>
    [HttpGet("shares")]
    public async Task<IActionResult> GetShares()
    {
        var user = await ResolveUserAsync(_userContextFactory);
        if (user is null) return ApiUnauthorized();

        var visible = new List<ShareDto>();

        // The caller's own home comes first as "user"; the client addresses it through RootPath.
        try
        {
            if (await _homes.EnsureHomeAsync(user.User) is { } home)
                visible.Add(new ShareDto(home.Share.Id, HomeFileBrowserViewModel.DisplayName, home.Share.IsRecycleEnabled, home.Path));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Home folder of {UserId} is unavailable", user.User.Id);
        }

        foreach (var share in await _shares.GetAllEnabledAsync())
        {
            // The home-folder share is only offered as the caller's own "user" entry above.
            if (share.IsShareHidden || share.IsUserHomes) continue;
            var fs = _fileServiceFactory.CreateForShare(share);
            if (await fs.CanListAsync(string.Empty, user))
                visible.Add(new ShareDto(share.Id, share.Name, share.IsRecycleEnabled));
        }

        return Ok(visible);
    }

    /// <summary>Lists the children of a directory (share root when path is omitted).</summary>
    [HttpGet("{shareId:guid}/list")]
    public async Task<IActionResult> List(Guid shareId, [FromQuery] string? path)
    {
        var (resolved, error) = await ResolveAsync(shareId, path);
        if (error is not null) return error;

        return await GuardAsync(async () =>
        {
            var items = await resolved!.Fs.ListAsync(resolved.Path, resolved.User);
            var dtos = items.Select(FileEntryDto.From).ToList();

            string etag = ComputeListEtag(dtos);
            if (RequestHasMatchingETag(etag))
                return StatusCode(StatusCodes.Status304NotModified);

            Response.Headers[HeaderNames.ETag] = etag;
            return Ok(dtos);
        });
    }

    /// <summary>Returns metadata for a single file or directory.</summary>
    [HttpGet("{shareId:guid}/metadata")]
    public async Task<IActionResult> Metadata(Guid shareId, [FromQuery] string? path)
    {
        var (resolved, error) = await ResolveAsync(shareId, path);
        if (error is not null) return error;

        return await GuardAsync(async () =>
        {
            var meta = await resolved!.Fs.GetMetadataAsync(resolved.Path, resolved.User);
            Response.Headers[HeaderNames.ETag] = ItemTag.For(meta);
            return Ok(FileEntryDto.From(meta));
        });
    }

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

    /// <summary>
    /// Creates a directory. Creating a directory that already exists is treated as
    /// success (204), so a retried creation is safe. Supports <c>Idempotency-Key</c>.
    /// </summary>
    [HttpPost("{shareId:guid}/directory")]
    public async Task<IActionResult> CreateDirectory(Guid shareId, [FromQuery] string? path)
    {
        var (resolved, error) = await ResolveAsync(shareId, path);
        if (error is not null) return error;

        if (string.IsNullOrEmpty(resolved!.Path))
            return ApiBadRequest("invalid_path", "A directory path is required.");
        var target = SanitizeTarget(resolved.Fs, resolved.Path);

        var fingerprint = RequestFingerprint.Compute("POST", $"{shareId}/{resolved.Path}", "mkdir");

        return await ExecuteIdempotentAsync(resolved.User.User.Id, fingerprint, () => GuardAsync(async () =>
        {
            // Already there (as a directory)? Nothing to do — keep creation idempotent.
            var current = await TryGetMetadataAsync(resolved.Fs, target, resolved.User);
            if (current is { IsDirectory: true })
                return NoContent();

            await resolved.Fs.CreateDirectoryAsync(target, resolved.User);
            return NoContent();
        }));
    }

    /// <summary>
    /// Renames or moves a file or directory within a share. Honors <c>If-Match</c>
    /// against the source item (mismatch → 412). A retry after the rename already
    /// applied — source gone, destination present — returns 204 rather than a
    /// spurious 404, and <c>Idempotency-Key</c> replays the stored result.
    /// </summary>
    [HttpPost("{shareId:guid}/rename")]
    public async Task<IActionResult> Rename(Guid shareId, [FromBody] RenameRequest request)
    {
        var user = await ResolveUserAsync(_userContextFactory);
        if (user is null) return ApiUnauthorized();

        var share = await _shares.GetByIdAsync(shareId);
        if (share is null || !share.IsEnabled) return ApiNotFound("Share not found.");

        if (request is null
            || !ShareRelativePath.TryNormalizeStrict(request.From, out var from, allowRoot: false)
            || !ShareRelativePath.TryNormalizeStrict(request.To, out var to, allowRoot: false))
            return ApiBadRequest("invalid_path", "Both 'from' and 'to' must be valid paths.");

        var fs = _fileServiceFactory.CreateForShare(share);
        var fingerprint = RequestFingerprint.Compute("POST", $"{shareId}/rename", from, to);
        to = SanitizeTarget(fs, to);

        return await ExecuteIdempotentAsync(user.User.Id, fingerprint, () => GuardAsync(async () =>
        {
            var fromMeta = await TryGetMetadataAsync(fs, from, user);
            if (fromMeta is null)
            {
                // Source gone: the rename may already have happened. If the
                // destination is present, treat the operation as applied.
                var toMeta = await TryGetMetadataAsync(fs, to, user);
                return toMeta is not null ? NoContent() : ApiNotFound("Source not found.");
            }

            var ifMatch = Request.Headers[HeaderNames.IfMatch].ToString();
            if (!ItemTag.Matches(ifMatch, fromMeta))
                return ApiPreconditionFailed();

            await fs.RenameAsync(from, to, user);
            return NoContent();
        }));
    }

    /// <summary>
    /// Copies a file or directory (recursively), possibly into another share. The copy keeps the
    /// source modification times. An existing target yields 409 <c>target_exists</c> unless
    /// <see cref="TransferRequest.Overwrite"/> is set (file replaced, folder merged); a file/folder
    /// clash is always 409 <c>type_mismatch</c>. The target's folder must exist (else 404
    /// <c>target_parent_missing</c>). 201 when the target was created, 204 when it already
    /// existed. Supports <c>Idempotency-Key</c>.
    /// </summary>
    [HttpPost("copy")]
    public Task<IActionResult> Copy([FromBody] TransferRequest request) => TransferAsync(request, isMove: false);

    /// <summary>
    /// Moves a file or directory, possibly into another share. Within a share it is an atomic
    /// rename (replacing a file or merging into a folder copies first); across shares the source is
    /// deleted only after the whole copy succeeded. Same conflict rules and status codes as copy.
    /// </summary>
    [HttpPost("move")]
    public Task<IActionResult> Move([FromBody] TransferRequest request) => TransferAsync(request, isMove: true);

    private async Task<IActionResult> TransferAsync(TransferRequest? request, bool isMove)
    {
        var user = await ResolveUserAsync(_userContextFactory);
        if (user is null) return ApiUnauthorized();

        if (request is null
            || !ShareRelativePath.TryNormalizeStrict(request.SourcePath, out var from, allowRoot: false)
            || !ShareRelativePath.TryNormalizeStrict(request.TargetPath, out var to, allowRoot: false))
            return ApiBadRequest("invalid_path", "Both 'sourcePath' and 'targetPath' must be valid paths.");

        var source = await _shares.GetByIdAsync(request.SourceShareId);
        var target = request.TargetShareId == request.SourceShareId
            ? source
            : await _shares.GetByIdAsync(request.TargetShareId);
        if (source is null || !source.IsEnabled || target is null || !target.IsEnabled)
            return ApiNotFound("Share not found.");

        var sameShare = source.Id == target.Id;
        // An ancestor as target would merge the source into itself (p/c onto p writes p/c/c to p/c).
        if (sameShare && (to == from
                || to.StartsWith(from + "/", StringComparison.Ordinal)
                || from.StartsWith(to + "/", StringComparison.Ordinal)))
            return ApiBadRequest("invalid_target", "The target must not be the source, lie inside it or contain it.");

        var sourceFs = _fileServiceFactory.CreateForShare(source);
        var targetFs = sameShare ? sourceFs : _fileServiceFactory.CreateForShare(target);
        var fingerprint = RequestFingerprint.Compute(
            "POST", isMove ? "move" : "copy", $"{source.Id}/{from}", $"{target.Id}/{to}",
            request.Overwrite ? "overwrite" : null);
        to = SanitizeTarget(targetFs, to);

        // ponytail: runs inside the request; add a job endpoint if proxies time out on huge folders.
        return await ExecuteIdempotentAsync(user.User.Id, fingerprint, () => GuardAsync(async () =>
        {
            var item = await WebDavController.FindAsync(sourceFs, from, user);
            if (item is null) return ApiNotFound("Source not found.");

            // The storage would create missing folders silently, unchecked and unlogged.
            if (!await WebDavController.ParentExistsAsync(targetFs, to, user))
                return NotFound(new ApiError("target_parent_missing", "The target folder does not exist."));

            var existing = await WebDavController.FindAsync(targetFs, to, user);
            if (existing is not null && existing.IsDirectory != item.IsDirectory)
                return Conflict(new ApiError("type_mismatch", "The target exists with a different type."));
            if (existing is not null && !request.Overwrite)
                return Conflict(new ApiError("target_exists", "The target already exists."));

            // Checked up front: a move that copies first would otherwise leave a duplicate behind.
            if (isMove && !await sourceFs.CanDeleteAsync(from, user))
                return ApiForbidden();

            if (isMove && sameShare && existing is null)
            {
                await sourceFs.RenameAsync(from, to, user);
            }
            else
            {
                // Replacing or merging copies first, so a failure (no write access, a busy path) leaves
                // the old target intact. A copy is a new item (new creation time, as in Explorer); a
                // move keeps its identity.
                await WebDavCopy.CopyAsync(sourceFs, from, targetFs, to, user, infinity: true,
                    HttpContext.RequestAborted, preserveTimes: true, preserveCreated: isMove);
                if (isMove)
                    await sourceFs.DeleteFileAsync(from, user, source.IsRecycleEnabled);
            }

            return existing is null ? StatusCode(StatusCodes.Status201Created) : NoContent();
        }));
    }

    /// <summary>
    /// Size of a file, or the recursive content size of a directory (what the web file browser
    /// shows). Computed on demand, so clients should request it only when it is displayed.
    /// </summary>
    [HttpGet("{shareId:guid}/size")]
    public async Task<IActionResult> Size(Guid shareId, [FromQuery] string? path)
    {
        var (resolved, error) = await ResolveAsync(shareId, path);
        if (error is not null) return error;

        return await GuardAsync(async () =>
        {
            var meta = await WebDavController.FindAsync(resolved!.Fs, resolved.Path, resolved.User);
            if (meta is null) return ApiNotFound();

            var size = meta.IsDirectory
                ? await resolved.Fs.GetDirectorySizeAsync(resolved.Path, resolved.User)
                : meta.Size;
            return Ok(new SizeDto(resolved.Path, meta.IsDirectory, size));
        });
    }

    /// <summary>
    /// Deletes a file or directory (moved to recycle bin when the share enables one).
    /// Honors <c>If-Match</c> (mismatch → 412) and is idempotent: deleting an item
    /// that is already gone returns 204, so a retried delete never yields a spurious
    /// 404. Supports <c>Idempotency-Key</c>.
    /// </summary>
    [HttpDelete("{shareId:guid}/item")]
    public async Task<IActionResult> Delete(Guid shareId, [FromQuery] string? path)
    {
        var (resolved, error) = await ResolveAsync(shareId, path, allowRoot: false);
        if (error is not null) return error;

        var fingerprint = RequestFingerprint.Compute("DELETE", $"{shareId}/{resolved!.Path}", "delete");

        return await ExecuteIdempotentAsync(resolved.User.User.Id, fingerprint, () => GuardAsync(async () =>
        {
            var current = await TryGetMetadataAsync(resolved.Fs, resolved.Path, resolved.User);
            if (current is null)
                return NoContent(); // already deleted — idempotent

            var ifMatch = Request.Headers[HeaderNames.IfMatch].ToString();
            if (!ItemTag.Matches(ifMatch, current))
                return ApiPreconditionFailed();

            await resolved.Fs.DeleteFileAsync(resolved.Path, resolved.User, resolved.Share.IsRecycleEnabled);
            return NoContent();
        }));
    }

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

    // ─────────────────────────── helpers ───────────────────────────

    private sealed record Resolved(UserContext User, ShareDefinition Share, IFileService Fs, string Path);

    private async Task<(Resolved? resolved, IActionResult? error)> ResolveAsync(
        Guid shareId, string? path, bool allowRoot = true)
    {
        var user = await ResolveUserAsync(_userContextFactory);
        if (user is null) return (null, ApiUnauthorized());

        var share = await _shares.GetByIdAsync(shareId);
        if (share is null || !share.IsEnabled) return (null, ApiNotFound("Share not found."));

        if (!ShareRelativePath.TryNormalizeStrict(path ?? string.Empty, out var normalized, allowRoot))
            return (null, ApiBadRequest("invalid_path", "The path is not a valid share-relative path."));

        var fs = _fileServiceFactory.CreateForShare(share);
        return (new Resolved(user, share, fs, normalized), null);
    }

    /// <summary>Maps the file service's exceptions to the uniform error envelope.</summary>
    private async Task<IActionResult> GuardAsync(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (UnauthorizedAccessException)
        {
            return ApiForbidden();
        }
        catch (FileNotFoundException)
        {
            return ApiNotFound();
        }
        catch (DirectoryNotFoundException)
        {
            return ApiNotFound();
        }
        catch (KeyNotFoundException)
        {
            return ApiNotFound();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new ApiError("conflict", ex.Message));
        }
        // A 409 is stored for an Idempotency-Key, so transient failures must not become one: every
        // retry with the same key would replay it.
        catch (BadHttpRequestException ex)
        {
            // The request body failed (too large → 413, truncated → 400), not the file operation.
            return StatusCode(ex.StatusCode, new ApiError(
                ex.StatusCode == StatusCodes.Status413PayloadTooLarge ? "payload_too_large" : "bad_request",
                ex.Message));
        }
        catch (IOException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            return ApiBadRequest("request_aborted", "The client aborted the request.");
        }
        catch (IOException ex) when (IsDiskFull(ex))
        {
            return StatusCode(StatusCodes.Status507InsufficientStorage, new ApiError("insufficient_storage", ex.Message));
        }
        catch (CloudSyncOperationConflictException ex)
        {
            Response.Headers[HeaderNames.RetryAfter] = "5";
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ApiError("path_busy", ex.Message));
        }
        catch (IOException ex)
        {
            // Target already exists, or another storage-level failure.
            return Conflict(new ApiError("conflict", ex.Message));
        }
    }

    /// <summary>ENOSPC / EDQUOT (.NET puts the raw errno into HResult on Unix), or the Win32 disk-full HRESULTs.</summary>
    private static bool IsDiskFull(IOException ex)
        => ex.HResult is 28 or 122 or unchecked((int)0x80070070) or unchecked((int)0x80070027);

    /// <summary>
    /// Metadata for a path, or <c>null</c> when it does not exist — used for
    /// precondition checks and idempotent "already applied" detection without
    /// letting a missing item surface as a 404 mid-operation. Access/other errors
    /// still propagate to <see cref="GuardAsync"/>.
    /// </summary>
    private static async Task<FileMetadata?> TryGetMetadataAsync(IFileService fs, string path, UserContext user)
    {
        try
        {
            return await fs.GetMetadataAsync(path, user);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>True when a header value is the wildcard <c>*</c> (ignoring whitespace).</summary>
    private static bool IsStar(string? headerValue) => headerValue?.Trim() == "*";

    private bool RequestHasMatchingETag(string etag)
        => Request.Headers.TryGetValue(HeaderNames.IfNoneMatch, out var values)
           && values.Any(v => v == etag || v == "*");

    private static string ComputeListEtag(IReadOnlyList<FileEntryDto> entries)
    {
        var sb = new StringBuilder();
        foreach (var e in entries.OrderBy(e => e.Path, StringComparer.Ordinal))
            sb.Append(e.Path).Append('|').Append(e.Size).Append('|')
              .Append(e.ModifiedAtUtc.Ticks).Append('\n');

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return $"\"{Convert.ToHexStringLower(hash)}\"";
    }
}
