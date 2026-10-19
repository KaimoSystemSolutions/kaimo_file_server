using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Web.Controllers.WebDav;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Kaimo_File_Server.Web.Controllers.Api;

public partial class BrowseApiController
{
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
}
