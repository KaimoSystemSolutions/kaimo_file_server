using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Xml.Linq;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Web.Controllers.Api;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
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
public sealed class WebDavController : ControllerBase
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
        var fs = _fileServiceFactory.CreateForShare(share.Id, share.Path);

        return await GuardAsync(async () =>
        {
            // Confirm the item really exists — GetMetadataAsync fabricates metadata for
            // a missing file, which would otherwise turn a 404 into a bogus 207.
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
                var fs = _fileServiceFactory.CreateForShare(share.Id, share.Path);
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
        if (error is not null) return error;

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
        if (error is not null) return error;

        if (IsLockedByOther(target!))
            return Locked();

        return await GuardAsync(async () =>
        {
            // The parent collection must exist (WebDAV: 409 otherwise).
            if (!await ParentExistsAsync(target.Fs, target.Path, target.User))
                return new StatusCodeResult(StatusCodes.Status409Conflict);

            var current = await FindAsync(target.Fs, target.Path, target.User);

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
        if (error is not null) return error;

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
        if (error is not null) return error;

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
        if (error is not null) return error;

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
        var destFs = _fileServiceFactory.CreateForShare(destShare.Id, destShare.Path);

        var overwrite = !string.Equals(
            Request.Headers["Overwrite"].ToString().Trim(), "F", StringComparison.OrdinalIgnoreCase);

        if (IsLockedByOther(source!) ||
            _locks.IsBlocked(destShare.Id, destPath, ParseSubmittedTokens()))
            return Locked();

        return await GuardAsync(async () =>
        {
            var destExisted = await FindAsync(destFs, destPath, source.User) is not null;
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
        if (error is not null) return error;

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

    // ─────────────────────────── LOCK / UNLOCK ───────────────────────────

    [AcceptVerbs("LOCK")]
    public async Task<IActionResult> Lock()
    {
        var (target, error) = await ResolveAsync(allowRoot: false);
        if (error is not null) return error;

        var settings = await _options.GetAsync();
        var timeout = ParseTimeout(settings.LockTimeoutSeconds);
        var body = await ReadBodyAsync();

        // No body → refresh of an existing lock, identified by the If header's token.
        if (string.IsNullOrWhiteSpace(body))
        {
            var token = ParseSubmittedTokens().FirstOrDefault();
            if (token is null)
                return new StatusCodeResult(StatusCodes.Status400BadRequest);

            var refreshed = _locks.Refresh(target.Share.Id, target.Path, token, timeout);
            if (refreshed is null)
                return new StatusCodeResult(StatusCodes.Status412PreconditionFailed);

            return LockResponse(refreshed, setTokenHeader: false);
        }

        var (ownerRaw, _) = ParseLockInfo(body);
        var depthInfinity = ParseDepth() != 0;
        var acquired = _locks.TryAcquire(target.Share.Id, target.Path, depthInfinity, ownerRaw, timeout);
        if (acquired is null)
            return Locked();

        return LockResponse(acquired, setTokenHeader: true, StatusCodes.Status200OK);
    }

    [AcceptVerbs("UNLOCK")]
    public async Task<IActionResult> Unlock()
    {
        var (target, error) = await ResolveAsync(allowRoot: false);
        if (error is not null) return error;

        var token = Request.Headers["Lock-Token"].ToString().Trim().Trim('<', '>');
        if (token.Length == 0)
            return new StatusCodeResult(StatusCodes.Status400BadRequest);

        await Task.CompletedTask;
        return _locks.Unlock(target.Share.Id, target.Path, token)
            ? new StatusCodeResult(StatusCodes.Status204NoContent)
            : new StatusCodeResult(StatusCodes.Status409Conflict);
    }

    // ─────────────────────────── helpers ───────────────────────────

    private sealed record Target(UserContext User, ShareDefinition Share, IFileService Fs, string Path);

    private async Task<(Target? target, IActionResult? error)> ResolveAsync(bool allowRoot = true)
    {
        var user = await ResolveUserAsync();
        if (user is null) return (null, Challenge());

        if (!WebDavPathResolver.TrySplit(Request.Path.Value, out var shareName, out var relativePath)
            || shareName.Length == 0)
            return (null, new StatusCodeResult(StatusCodes.Status404NotFound));

        if (!allowRoot && relativePath.Length == 0)
            return (null, new StatusCodeResult(StatusCodes.Status403Forbidden));

        var share = await GetVisibleShareAsync(shareName);
        if (share is null) return (null, new StatusCodeResult(StatusCodes.Status404NotFound));

        var fs = _fileServiceFactory.CreateForShare(share.Id, share.Path);
        return (new Target(user, share, fs, relativePath), null);
    }

    /// <summary>
    /// Rebuilds the caller's <see cref="UserContext"/> from the authenticated
    /// principal (Basic or Bearer), re-checking existence and the enabled flag, and
    /// — for device-scoped bearer tokens — that the device is still active. Returns
    /// <c>null</c> when the account is gone/disabled or the device was revoked.
    /// </summary>
    private async Task<UserContext?> ResolveUserAsync()
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return null;

        if (Guid.TryParse(User.FindFirstValue(JwtTokenService.DeviceIdClaim), out var deviceId))
        {
            var devices = HttpContext.RequestServices.GetRequiredService<ISyncDeviceRepository>();
            var device = await devices.GetByIdAsync(deviceId);
            if (device is null || !device.IsActive)
                return null;
        }

        var context = await _userContextFactory.CreateByUserIdAsync(userId);
        return context is { User.IsEnabled: true } ? context : null;
    }

    /// <summary>A share addressable over WebDAV: enabled and known. Hidden shares stay reachable by direct URL.</summary>
    private async Task<ShareDefinition?> GetVisibleShareAsync(string name)
    {
        var share = await _shares.GetByNameAsync(name);
        return share is { IsEnabled: true } ? share : null;
    }

    /// <summary>Maps the file service's exceptions to WebDAV status codes (same mapping as the REST browse API).</summary>
    private async Task<IActionResult> GuardAsync(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (UnauthorizedAccessException) { return new StatusCodeResult(StatusCodes.Status403Forbidden); }
        catch (FileNotFoundException) { return new StatusCodeResult(StatusCodes.Status404NotFound); }
        catch (DirectoryNotFoundException) { return new StatusCodeResult(StatusCodes.Status404NotFound); }
        catch (KeyNotFoundException) { return new StatusCodeResult(StatusCodes.Status404NotFound); }
        catch (InvalidOperationException) { return new StatusCodeResult(StatusCodes.Status409Conflict); }
        // A storage-level failure (e.g. the target directory is not writable by the
        // container's user) is not the client's fault — report 409 rather than letting
        // it surface as an unhandled 500.
        catch (IOException) { return new StatusCodeResult(StatusCodes.Status409Conflict); }
    }

    /// <summary>
    /// Metadata for an existing item, or <c>null</c> when it does not exist.
    /// <see cref="IFileService.GetMetadataAsync"/> cannot be used as an existence
    /// probe: the file-system storage fabricates metadata for a missing file instead
    /// of throwing. Existence is therefore confirmed by listing the parent (which is
    /// also how a WebDAV client reaches the item), and the returned entry carries the
    /// real size/mtime an <c>If-Match</c> needs. The share root always exists.
    /// </summary>
    private static async Task<FileMetadata?> FindAsync(IFileService fs, string path, UserContext user)
    {
        if (path.Length == 0)
            return await fs.GetMetadataAsync(string.Empty, user);

        var parent = ShareRelativePath.GetParent(path);
        var name = ShareRelativePath.GetFileName(path);
        List<FileMetadata> siblings;
        try { siblings = await fs.ListAsync(parent, user); }
        catch (DirectoryNotFoundException) { return null; } // parent itself is missing
        catch (FileNotFoundException) { return null; }
        return siblings.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));
    }

    /// <summary>True when the parent collection of <paramref name="path"/> exists (the share root always does).</summary>
    private static async Task<bool> ParentExistsAsync(IFileService fs, string path, UserContext user)
    {
        var parent = ShareRelativePath.GetParent(path);
        return parent.Length == 0 || await FindAsync(fs, parent, user) is { IsDirectory: true };
    }

    private bool IsLockedByOther(Target target)
        => _locks.IsBlocked(target.Share.Id, target.Path, ParseSubmittedTokens());

    private IReadOnlyCollection<string> ParseSubmittedTokens()
        => (IReadOnlyCollection<string>)WebDavIfHeader.ParseLockTokens(Request.Headers["If"].ToString());

    private IActionResult Locked() => new StatusCodeResult(StatusCodes.Status423Locked);

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

    private int ParseTimeout(int defaultSeconds)
    {
        foreach (var part in Request.Headers["Timeout"].ToString().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith("Second-", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(part["Second-".Length..], out var seconds))
                return Math.Clamp(seconds, 1, WebDavOptions.MaxLockTimeoutSeconds);
        }
        return defaultSeconds;
    }

    private IActionResult LockResponse(WebDavLockManager.LockInfo info, bool setTokenHeader, int status = StatusCodes.Status200OK)
    {
        var active = new WebDavActiveLock(info.Token, info.DepthInfinity, _locks.RemainingSeconds(info), info.OwnerRaw);
        var prop = new XElement(Dav + "prop",
            new XAttribute(XNamespace.Xmlns + "D", Dav.NamespaceName),
            BuildLockDiscovery(active));

        if (setTokenHeader)
            Response.Headers["Lock-Token"] = $"<{info.Token}>";

        return new ContentResult
        {
            StatusCode = status,
            ContentType = "application/xml; charset=\"utf-8\"",
            Content = SerializeElement(prop),
        };
    }

    private static XElement BuildLockDiscovery(WebDavActiveLock active)
        => new(Dav + "lockdiscovery",
            new XElement(Dav + "activelock",
                new XElement(Dav + "locktype", new XElement(Dav + "write")),
                new XElement(Dav + "lockscope", new XElement(Dav + "exclusive")),
                new XElement(Dav + "depth", active.DepthInfinity ? "infinity" : "0"),
                active.OwnerRaw is null ? null : new XElement(Dav + "owner", active.OwnerRaw),
                new XElement(Dav + "timeout", $"Second-{active.RemainingSeconds}"),
                new XElement(Dav + "locktoken", new XElement(Dav + "href", active.Token))));

    private WebDavEntry ToEntry(ShareDefinition share, string relativePath, FileMetadata meta)
        => new(
            BuildHref(share.Name, relativePath, meta.IsDirectory),
            meta.Name.Length > 0 ? meta.Name : share.Name,
            meta.IsDirectory,
            meta.Size,
            meta.ModifiedAt,
            meta.CreatedAt,
            meta.IsDirectory ? null : ItemTag.For(meta),
            _locks.GetLocksAt(share.Id, relativePath)
                .Select(l => new WebDavActiveLock(l.Token, l.DepthInfinity, _locks.RemainingSeconds(l), l.OwnerRaw))
                .ToList());

    private static string BuildHref(string shareName, string relativePath, bool isCollection)
    {
        var builder = new StringBuilder("/dav/");
        builder.Append(Uri.EscapeDataString(shareName));
        if (relativePath.Length > 0)
        {
            foreach (var segment in relativePath.Split('/'))
                builder.Append('/').Append(Uri.EscapeDataString(segment));
        }
        if (isCollection)
            builder.Append('/');
        return builder.ToString();
    }

    private async Task<string> ReadBodyAsync()
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private IActionResult MultiStatus(string xml) => new ContentResult
    {
        StatusCode = StatusCodes.Status207MultiStatus,
        ContentType = "application/xml; charset=\"utf-8\"",
        Content = xml,
    };

    private IActionResult DavError(int status, string conditionLocalName)
    {
        var error = new XElement(Dav + "error",
            new XAttribute(XNamespace.Xmlns + "D", Dav.NamespaceName),
            new XElement(Dav + conditionLocalName));
        return new ContentResult
        {
            StatusCode = status,
            ContentType = "application/xml; charset=\"utf-8\"",
            Content = SerializeElement(error),
        };
    }

    private static string SerializeMultiStatus(XElement response)
    {
        var multistatus = new XElement(Dav + "multistatus",
            new XAttribute(XNamespace.Xmlns + "D", Dav.NamespaceName), response);
        return SerializeElement(multistatus);
    }

    private static string SerializeElement(XElement root) => WebDavXml.Serialize(root);

    private static List<XName> ParseProppatchProps(string? body)
    {
        var names = new List<XName>();
        if (string.IsNullOrWhiteSpace(body)) return names;

        XDocument doc;
        try { doc = XDocument.Parse(body); }
        catch (System.Xml.XmlException) { return names; }

        foreach (var prop in doc.Descendants(Dav + "prop"))
            names.AddRange(prop.Elements().Select(e => e.Name));
        return names;
    }

    private static DateTime? TryReadWin32ModifiedTime(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        XDocument doc;
        try { doc = XDocument.Parse(body); }
        catch (System.Xml.XmlException) { return null; }

        var value = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Win32LastModifiedTime")?.Value;
        if (string.IsNullOrWhiteSpace(value)) return null;

        return DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }

    private static (string? ownerRaw, bool exclusive) ParseLockInfo(string body)
    {
        try
        {
            var doc = XDocument.Parse(body);
            var owner = doc.Descendants(Dav + "owner").FirstOrDefault();
            // Keep the owner's inner XML verbatim so lockdiscovery can echo it back.
            var ownerRaw = owner is null
                ? null
                : string.Concat(owner.Nodes().Select(n => n.ToString(SaveOptions.DisableFormatting)));
            var exclusive = doc.Descendants(Dav + "exclusive").Any() || !doc.Descendants(Dav + "shared").Any();
            return (string.IsNullOrWhiteSpace(ownerRaw) ? null : ownerRaw, exclusive);
        }
        catch (System.Xml.XmlException)
        {
            return (null, true);
        }
    }
}
