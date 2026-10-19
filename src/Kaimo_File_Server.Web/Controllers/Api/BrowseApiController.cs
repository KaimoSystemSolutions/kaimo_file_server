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
public sealed partial class BrowseApiController : ApiControllerBase
{
    /// <summary>Optional upload header carrying the source mtime as Unix epoch milliseconds (UTC).</summary>
    public const string ModifiedAtHeader = "X-Kaimo-Modified-At";

    /// <summary>
    /// Optional upload / stamp header carrying the source creation time as Unix epoch milliseconds
    /// (UTC); also returned on downloads so a client can give its copy the same creation time.
    /// </summary>
    public const string CreatedAtHeader = "X-Kaimo-Created-At";

    /// <summary>
    /// Optional upload / stamp header carrying the source last-access time as Unix epoch
    /// milliseconds (UTC); also returned on downloads so a client can give its copy the same one.
    /// </summary>
    public const string AccessedAtHeader = "X-Kaimo-Accessed-At";

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
