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
            Response.Headers[HeaderNames.LastModified] =
                meta.ModifiedAt.ToUniversalTime().ToString("R");
            // enableRangeProcessing lets clients resume/segment large downloads.
            return File(stream, "application/octet-stream",
                fileDownloadName: meta.Name,
                enableRangeProcessing: true);
        });
    }

    /// <summary>Uploads (creates or overwrites) a file from the request body.</summary>
    [HttpPut("{shareId:guid}/content")]
    public async Task<IActionResult> Upload(Guid shareId, [FromQuery] string? path, CancellationToken ct)
    {
        var (resolved, error) = await ResolveAsync(shareId, path);
        if (error is not null) return error;

        if (string.IsNullOrEmpty(resolved!.Path))
            return ApiBadRequest("invalid_path", "A file path is required.");

        return await GuardAsync(async () =>
        {
            await resolved.Fs.WriteFileAsync(resolved.Path, Request.Body, resolved.User, ct);
            var meta = await resolved.Fs.GetMetadataAsync(resolved.Path, resolved.User);
            return Ok(FileEntryDto.From(meta));
        });
    }

    /// <summary>Creates a directory.</summary>
    [HttpPost("{shareId:guid}/directory")]
    public async Task<IActionResult> CreateDirectory(Guid shareId, [FromQuery] string? path)
    {
        var (resolved, error) = await ResolveAsync(shareId, path);
        if (error is not null) return error;

        if (string.IsNullOrEmpty(resolved!.Path))
            return ApiBadRequest("invalid_path", "A directory path is required.");

        return await GuardAsync(async () =>
        {
            await resolved.Fs.CreateDirectoryAsync(resolved.Path, resolved.User);
            return NoContent();
        });
    }

    /// <summary>Renames or moves a file or directory within a share.</summary>
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
        return await GuardAsync(async () =>
        {
            await fs.RenameAsync(from, to, user);
            return NoContent();
        });
    }

    /// <summary>Deletes a file or directory (moved to recycle bin when the share enables one).</summary>
    [HttpDelete("{shareId:guid}/item")]
    public async Task<IActionResult> Delete(Guid shareId, [FromQuery] string? path)
    {
        var (resolved, error) = await ResolveAsync(shareId, path, allowRoot: false);
        if (error is not null) return error;

        return await GuardAsync(async () =>
        {
            await resolved!.Fs.DeleteFileAsync(resolved.Path, resolved.User, resolved.Share.IsRecycleEnabled);
            return NoContent();
        });
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
