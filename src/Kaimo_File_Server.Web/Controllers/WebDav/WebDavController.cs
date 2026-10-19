using System.Xml.Linq;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Web.Controllers.Api;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Kaimo_File_Server.Web.Controllers.WebDav;

/// <summary>
/// RFC 4918 (class 1, 2, 3) WebDAV server mounted at <c>/dav</c>. It is a second
/// first-class file transport with the same reach as SMB: every method delegates
/// to the ACL-checked <see cref="IFileService"/>, so shares, per-item ACLs, the
/// recycle bin, versioning, search indexing, the client change feed and read-only
/// demo mode all keep working — WebDAV is only a new transport, nothing in Core
/// changes.
/// </summary>
[Authorize(AuthenticationSchemes = WebDavBasicAuthenticationHandler.SchemeName + "," + JwtBearerDefaults.AuthenticationScheme)]
[Route("dav/{**path}")]
public sealed partial class WebDavController : ControllerBase
{
    private const string AllowedMethods =
        "OPTIONS, GET, HEAD, PROPFIND, PROPPATCH, PUT, DELETE, MKCOL, COPY, MOVE, LOCK, UNLOCK";

    private static readonly XNamespace Dav = WebDavPropFind.Dav;

    private readonly IUserContextFactory _userContextFactory;
    private readonly IShareRepository _shares;
    private readonly IFileServiceFactory _fileServiceFactory;
    private readonly WebDavLockManager _locks;
    private readonly WebDavOptions _options;

    public WebDavController(
        IUserContextFactory userContextFactory,
        IShareRepository shares,
        IFileServiceFactory fileServiceFactory,
        WebDavLockManager locks,
        WebDavOptions options)
    {
        _userContextFactory = userContextFactory;
        _shares = shares;
        _fileServiceFactory = fileServiceFactory;
        _locks = locks;
        _options = options;
    }

    // ─────────────────────────── OPTIONS ───────────────────────────

    /// <summary>Advertises capability headers. Allowed anonymously — it returns headers only.</summary>
    [AllowAnonymous]
    [AcceptVerbs("OPTIONS")]
    public IActionResult Options()
    {
        Response.Headers["DAV"] = "1, 2, 3";
        Response.Headers["MS-Author-Via"] = "DAV";
        Response.Headers[HeaderNames.Allow] = AllowedMethods;
        Response.Headers[HeaderNames.AcceptRanges] = "bytes";
        return new StatusCodeResult(StatusCodes.Status200OK);
    }

    // ─────────────────────────── PROPFIND ───────────────────────────

    [AcceptVerbs("PROPFIND")]
    public async Task<IActionResult> PropFind()
    {
        var user = await ResolveUserAsync();
        if (user is null) return Challenge(WebDavBasicAuthenticationHandler.SchemeName);

        var depth = ParseDepth();
        if (depth is null)
            return DavError(StatusCodes.Status403Forbidden, "propfind-finite-depth");

        var request = WebDavPropFind.ParseRequest(await ReadBodyAsync());

        if (!WebDavPathResolver.TrySplit(Request.Path.Value, out var shareName, out var relativePath))
            return new StatusCodeResult(StatusCodes.Status400BadRequest);

        // Root collection: list the shares the caller may see.
        if (shareName.Length == 0)
            return await PropFindRootAsync(user, depth.Value, request);

        var share = await GetVisibleShareAsync(shareName);
        if (share is null) return new StatusCodeResult(StatusCodes.Status404NotFound);
        var fs = _fileServiceFactory.CreateForShare(share);

        return await GuardAsync(async () =>
        {
            // Confirm the item really exists via the parent listing (null → 404).
            var meta = relativePath.Length == 0
                ? await fs.GetMetadataAsync(string.Empty, user)
                : await FindAsync(fs, relativePath, user);
            if (meta is null)
                return new StatusCodeResult(StatusCodes.Status404NotFound);

            var entries = new List<WebDavEntry> { ToEntry(share, relativePath, meta) };

            if (depth.Value == 1 && meta.IsDirectory)
            {
                foreach (var child in await fs.ListAsync(relativePath, user))
                    entries.Add(ToEntry(share, child.Path, child));
            }

            return MultiStatus(WebDavPropFind.WriteMultiStatus(entries, request));
        });
    }

    private async Task<IActionResult> PropFindRootAsync(
        UserContext user, int depth, WebDavPropFind.PropRequest request)
    {
        var now = DateTime.UtcNow;
        var entries = new List<WebDavEntry>
        {
            new("/dav/", "dav", IsCollection: true, 0, now, now, null, []),
        };

        if (depth == 1)
        {
            foreach (var share in await _shares.GetAllEnabledAsync())
            {
                if (share.IsShareHidden) continue;
                var fs = _fileServiceFactory.CreateForShare(share);
                if (!await fs.CanListAsync(string.Empty, user)) continue;
                entries.Add(new WebDavEntry(
                    BuildHref(share.Name, string.Empty, isCollection: true),
                    share.Name, IsCollection: true, 0, now, now, null, []));
            }
        }

        return MultiStatus(WebDavPropFind.WriteMultiStatus(entries, request));
    }

    // ─────────────────────────── GET / HEAD ───────────────────────────

    [AcceptVerbs("GET", "HEAD")]
    public async Task<IActionResult> Get()
    {
        var (target, error) = await ResolveAsync();
        if (target is null) return error!;

        return await GuardAsync(async () =>
        {
            var meta = await target!.Fs.GetMetadataAsync(target.Path, target.User);
            if (meta.IsDirectory)
            {
                Response.Headers[HeaderNames.Allow] = AllowedMethods;
                return new StatusCodeResult(StatusCodes.Status405MethodNotAllowed);
            }

            var stream = await target.Fs.ReadFileAsync(target.Path, target.User);
            Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
            Response.Headers[HeaderNames.ETag] = ItemTag.For(meta);
            Response.Headers[HeaderNames.LastModified] = meta.ModifiedAt.ToUniversalTime().ToString("R");
            return File(stream, "application/octet-stream",
                fileDownloadName: meta.Name, enableRangeProcessing: true);
        });
    }

    // ─────────────────────────── PUT ───────────────────────────

    [DisableRequestSizeLimit]
    [AcceptVerbs("PUT")]
    public async Task<IActionResult> Put(CancellationToken ct)
    {
        var (target, error) = await ResolveAsync(allowRoot: false);
        if (target is null) return error!;

        if (IsLockedByOther(target!))
            return Locked();

        // A PUT body is the raw resource. A form Content-Type makes MVC's form value provider
        // drain Request.Body during model binding, so WriteFileAsync below would persist an
        // EMPTY file and report success — a silent, destructive overwrite. A raw PUT is never
        // a form, so reject it outright instead of truncating the target.
        if (Request.HasFormContentType)
            return new StatusCodeResult(StatusCodes.Status415UnsupportedMediaType);

        return await GuardAsync(async () =>
        {
            var current = await FindAsync(target.Fs, target.Path, target.User);
            if (current is null && !IsCreatableName(target.Path))
                return new StatusCodeResult(StatusCodes.Status400BadRequest);

            // The parent collection must exist (WebDAV: 409 otherwise).
            if (!await ParentExistsAsync(target.Fs, target.Path, target.User))
                return new StatusCodeResult(StatusCodes.Status409Conflict);

            var ifNoneMatch = Request.Headers[HeaderNames.IfNoneMatch].ToString();
            var ifMatch = Request.Headers[HeaderNames.IfMatch].ToString();
            if (ifNoneMatch.Trim() == "*" && current is not null)
                return new StatusCodeResult(StatusCodes.Status412PreconditionFailed);
            if (!string.IsNullOrWhiteSpace(ifMatch) && (current is null || !ItemTag.Matches(ifMatch, current)))
                return new StatusCodeResult(StatusCodes.Status412PreconditionFailed);

            await target.Fs.WriteFileAsync(target.Path, Request.Body, target.User, ct);

            var meta = await target.Fs.GetMetadataAsync(target.Path, target.User);
            Response.Headers[HeaderNames.ETag] = ItemTag.For(meta);
            return new StatusCodeResult(
                current is null ? StatusCodes.Status201Created : StatusCodes.Status204NoContent);
        });
    }

    // ─────────────────────────── MKCOL ───────────────────────────

    [AcceptVerbs("MKCOL")]
    public async Task<IActionResult> MkCol()
    {
        var (target, error) = await ResolveAsync(allowRoot: false);
        if (target is null) return error!;

        // MKCOL takes no request body.
        if ((Request.ContentLength ?? 0) > 0)
            return new StatusCodeResult(StatusCodes.Status415UnsupportedMediaType);

        if (IsLockedByOther(target!))
            return Locked();

        return await GuardAsync(async () =>
        {
            if (await FindAsync(target.Fs, target.Path, target.User) is not null)
            {
                Response.Headers[HeaderNames.Allow] = AllowedMethods;
                return new StatusCodeResult(StatusCodes.Status405MethodNotAllowed);
            }

            if (!IsCreatableName(target.Path))
                return new StatusCodeResult(StatusCodes.Status400BadRequest);

            if (!await ParentExistsAsync(target.Fs, target.Path, target.User))
                return new StatusCodeResult(StatusCodes.Status409Conflict);

            await target.Fs.CreateDirectoryAsync(target.Path, target.User);
            return new StatusCodeResult(StatusCodes.Status201Created);
        });
    }

    // ─────────────────────────── DELETE ───────────────────────────

    [AcceptVerbs("DELETE")]
    public async Task<IActionResult> Delete()
    {
        var (target, error) = await ResolveAsync(allowRoot: false);
        if (target is null) return error!;

        if (IsLockedByOther(target!))
            return Locked();

        return await GuardAsync(async () =>
        {
            if (await FindAsync(target.Fs, target.Path, target.User) is null)
                return new StatusCodeResult(StatusCodes.Status404NotFound);

            await target.Fs.DeleteFileAsync(target.Path, target.User, target.Share.IsRecycleEnabled);
            return new StatusCodeResult(StatusCodes.Status204NoContent);
        });
    }

    // ─────────────────────────── MOVE / COPY ───────────────────────────

    [AcceptVerbs("MOVE")]
    public Task<IActionResult> Move() => MoveOrCopyAsync(isMove: true);

    [AcceptVerbs("COPY")]
    public Task<IActionResult> Copy() => MoveOrCopyAsync(isMove: false);

    private async Task<IActionResult> MoveOrCopyAsync(bool isMove)
    {
        var (source, error) = await ResolveAsync(allowRoot: false);
        if (source is null) return error!;

        var outcome = WebDavPathResolver.TryResolveDestination(
            Request.Headers["Destination"].ToString(), Request.Host.Value ?? string.Empty,
            out var destShareName, out var destPath);

        if (outcome == WebDavPathResolver.DestinationOutcome.ForeignHost)
            return new StatusCodeResult(StatusCodes.Status502BadGateway);
        if (outcome != WebDavPathResolver.DestinationOutcome.Ok || destPath.Length == 0)
            return new StatusCodeResult(StatusCodes.Status400BadRequest);

        var destShare = await GetVisibleShareAsync(destShareName);
        if (destShare is null)
            return new StatusCodeResult(StatusCodes.Status409Conflict);
        var destFs = _fileServiceFactory.CreateForShare(destShare);

        var overwrite = !string.Equals(
            Request.Headers["Overwrite"].ToString().Trim(), "F", StringComparison.OrdinalIgnoreCase);

        if (IsLockedByOther(source!) ||
            _locks.IsBlocked(destShare.Id, destPath, ParseSubmittedTokens()))
            return Locked();

        return await GuardAsync(async () =>
        {
            var destExisted = await FindAsync(destFs, destPath, source.User) is not null;
            if (!destExisted && !IsCreatableName(destPath))
                return new StatusCodeResult(StatusCodes.Status400BadRequest);
            if (destExisted && !overwrite)
                return new StatusCodeResult(StatusCodes.Status412PreconditionFailed);

            // COPY honors Depth (0 = collection only, default infinity); MOVE is always deep.
            var infinity = isMove || ParseDepth() != 0;

            if (destExisted && overwrite)
                await destFs.DeleteFileAsync(destPath, source.User, destShare.IsRecycleEnabled);

            if (isMove && source.Share.Id == destShare.Id)
            {
                await source.Fs.RenameAsync(source.Path, destPath, source.User);
            }
            else
            {
                await WebDavCopy.CopyAsync(
                    source.Fs, source.Path, destFs, destPath, source.User, infinity, HttpContext.RequestAborted);
                if (isMove)
                    await source.Fs.DeleteFileAsync(source.Path, source.User, source.Share.IsRecycleEnabled);
            }

            return new StatusCodeResult(
                destExisted ? StatusCodes.Status204NoContent : StatusCodes.Status201Created);
        });
    }

    // ─────────────────────────── PROPPATCH ───────────────────────────

    [AcceptVerbs("PROPPATCH")]
    public async Task<IActionResult> PropPatch()
    {
        var (target, error) = await ResolveAsync(allowRoot: false);
        if (target is null) return error!;

        if (IsLockedByOther(target!))
            return Locked();

        var body = await ReadBodyAsync();

        return await GuardAsync(async () =>
        {
            var meta = await target.Fs.GetMetadataAsync(target.Path, target.User);

            // The only property we persist is the last-modified time Office sets after a save.
            // Every other Win32* property is accepted and ignored (reported 200), which is what
            // Explorer and Office expect.
            var requested = ParseProppatchProps(body);
            var modified = TryReadWin32ModifiedTime(body);
            if (modified is { } when)
                await target.Fs.SetModifiedAtAsync(target.Path, target.User, when);

            var props = requested.Count > 0 ? requested : [Dav + "getlastmodified"];
            var propElements = props.Select(name => new XElement(name));
            var response = new XElement(Dav + "response",
                new XElement(Dav + "href", BuildHref(target.Share.Name, target.Path, meta.IsDirectory)),
                new XElement(Dav + "propstat",
                    new XElement(Dav + "prop", propElements),
                    new XElement(Dav + "status", "HTTP/1.1 200 OK")));
            return MultiStatus(SerializeMultiStatus(response));
        });
    }

    // ─────────────────────────── helpers ───────────────────────────

    /// <summary>Parses the <c>Depth</c> header: 0 or 1, or <c>null</c> for infinity/forbidden. Absent defaults to 1.</summary>
    private int? ParseDepth()
    {
        var raw = Request.Headers["Depth"].ToString().Trim();
        return raw switch
        {
            "0" => 0,
            "1" => 1,
            "" => 1,
            _ => null, // "infinity" (or anything else) is refused where the caller checks for null
        };
    }

}
