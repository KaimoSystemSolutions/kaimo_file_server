using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc;

namespace Kaimo_File_Server.Web.Controllers.WebDav;

public partial class WebDavController
{
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

    private bool IsLockedByOther(Target target)
        => _locks.IsBlocked(target.Share.Id, target.Path, ParseSubmittedTokens());

    private IReadOnlyCollection<string> ParseSubmittedTokens()
        => (IReadOnlyCollection<string>)WebDavIfHeader.ParseLockTokens(Request.Headers["If"].ToString());

    private IActionResult Locked() => new StatusCodeResult(StatusCodes.Status423Locked);

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
