using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services.File;
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
    private readonly IUserContextFactory _userContextFactory;
    private readonly IShareRepository _shares;
    private readonly IFileServiceFactory _fileServiceFactory;

    public BrowseApiController(
        IUserContextFactory userContextFactory,
        IShareRepository shares,
        IFileServiceFactory fileServiceFactory)
    {
        _userContextFactory = userContextFactory;
        _shares = shares;
        _fileServiceFactory = fileServiceFactory;
    }

    /// <summary>Lists the shares the caller may browse.</summary>
    [HttpGet("shares")]
    public async Task<IActionResult> GetShares()
    {
        var user = await ResolveUserAsync(_userContextFactory);
        if (user is null) return ApiUnauthorized();

        var visible = new List<ShareDto>();
        foreach (var share in await _shares.GetAllEnabledAsync())
        {
            if (share.IsShareHidden) continue;
            var fs = _fileServiceFactory.CreateForShare(share.Id, share.Path);
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

    /// <summary>Downloads a file's content. Supports HTTP Range for resumable transfers.</summary>
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

            var stream = await resolved.Fs.ReadFileAsync(resolved.Path, resolved.User);
            Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
            Response.Headers[HeaderNames.ETag] = ItemTag.For(meta);
            Response.Headers[HeaderNames.LastModified] =
                meta.ModifiedAt.ToUniversalTime().ToString("R");
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
    /// so a retried upload replays its stored result instead of re-writing.
    /// </summary>
    [HttpPut("{shareId:guid}/content")]
    public async Task<IActionResult> Upload(Guid shareId, [FromQuery] string? path, CancellationToken ct)
    {
        var (resolved, error) = await ResolveAsync(shareId, path);
        if (error is not null) return error;

        if (string.IsNullOrEmpty(resolved!.Path))
            return ApiBadRequest("invalid_path", "A file path is required.");

        var fingerprint = RequestFingerprint.Compute(
            "PUT", $"{shareId}/{resolved.Path}", "len:" + (Request.ContentLength?.ToString() ?? "?"));

        return await ExecuteIdempotentAsync(resolved.User.User.Id, fingerprint, () => GuardAsync(async () =>
        {
            var ifMatch = Request.Headers[HeaderNames.IfMatch].ToString();
            var createOnly = IsStar(Request.Headers[HeaderNames.IfNoneMatch].ToString());

            if (createOnly || !string.IsNullOrWhiteSpace(ifMatch))
            {
                var current = await TryGetMetadataAsync(resolved.Fs, resolved.Path, resolved.User);
                if (createOnly && current is not null)
                    return ApiPreconditionFailed("The file already exists.");
                if (!string.IsNullOrWhiteSpace(ifMatch) && (current is null || !ItemTag.Matches(ifMatch, current)))
                    return ApiPreconditionFailed();
            }

            await resolved.Fs.WriteFileAsync(resolved.Path, Request.Body, resolved.User, ct);
            var meta = await resolved.Fs.GetMetadataAsync(resolved.Path, resolved.User);
            Response.Headers[HeaderNames.ETag] = ItemTag.For(meta);
            return Ok(FileEntryDto.From(meta));
        }));
    }

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

        var fingerprint = RequestFingerprint.Compute("POST", $"{shareId}/{resolved.Path}", "mkdir");

        return await ExecuteIdempotentAsync(resolved.User.User.Id, fingerprint, () => GuardAsync(async () =>
        {
            // Already there (as a directory)? Nothing to do — keep creation idempotent.
            var current = await TryGetMetadataAsync(resolved.Fs, resolved.Path, resolved.User);
            if (current is { IsDirectory: true })
                return NoContent();

            await resolved.Fs.CreateDirectoryAsync(resolved.Path, resolved.User);
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

        var fs = _fileServiceFactory.CreateForShare(share.Id, share.Path);
        var fingerprint = RequestFingerprint.Compute("POST", $"{shareId}/rename", from, to);

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

        var fs = _fileServiceFactory.CreateForShare(share.Id, share.Path);
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
    }

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
