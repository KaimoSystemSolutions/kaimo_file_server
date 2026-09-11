using System.Globalization;
using System.Xml.Linq;

namespace Kaimo_File_Server.Web.Controllers.WebDav;

/// <summary>
/// An active exclusive-write lock as reported in a <c>lockdiscovery</c> property.
/// </summary>
public sealed record WebDavActiveLock(string Token, bool DepthInfinity, int RemainingSeconds, string? OwnerRaw);

/// <summary>
/// One resource in a PROPFIND response. Collections omit content length / type /
/// etag; files carry all three.
/// </summary>
public sealed record WebDavEntry(
    string Href,
    string DisplayName,
    bool IsCollection,
    long ContentLength,
    DateTime LastModifiedUtc,
    DateTime CreationTimeUtc,
    string? ETag,
    IReadOnlyList<WebDavActiveLock> Locks);

/// <summary>
/// Parses PROPFIND/PROPPATCH request bodies and writes the <c>207 Multi-Status</c>
/// XML. Only the property set clients actually consume is implemented
/// (RFC 4918 §15 live properties plus <c>supportedlock</c>/<c>lockdiscovery</c>);
/// any other named property is reported as <c>404</c> inside the multistatus,
/// which is what the specification requires and what clients expect.
/// </summary>
public static class WebDavPropFind
{
    public static readonly XNamespace Dav = "DAV:";

    /// <summary>How a PROPFIND asked for properties.</summary>
    public enum PropMode { AllProp, PropName, Named }

    /// <summary>Parsed PROPFIND request: the mode plus the explicitly named properties.</summary>
    public sealed record PropRequest(PropMode Mode, IReadOnlyList<XName> Named);

    private static readonly XName[] SupportedProps =
    [
        Dav + "resourcetype", Dav + "displayname", Dav + "getcontentlength",
        Dav + "getcontenttype", Dav + "getlastmodified", Dav + "creationdate",
        Dav + "getetag", Dav + "supportedlock", Dav + "lockdiscovery",
    ];

    /// <summary>
    /// Parses a PROPFIND body. An empty body means "all properties" per RFC 4918.
    /// A malformed body is treated as <see cref="PropMode.AllProp"/> so a lenient
    /// client still gets a useful listing.
    /// </summary>
    public static PropRequest ParseRequest(string? xmlBody)
    {
        if (string.IsNullOrWhiteSpace(xmlBody))
            return new PropRequest(PropMode.AllProp, []);

        XDocument doc;
        try { doc = XDocument.Parse(xmlBody); }
        catch (System.Xml.XmlException) { return new PropRequest(PropMode.AllProp, []); }

        var root = doc.Root;
        if (root is null || root.Name != Dav + "propfind")
            return new PropRequest(PropMode.AllProp, []);

        if (root.Element(Dav + "allprop") is not null)
            return new PropRequest(PropMode.AllProp, []);
        if (root.Element(Dav + "propname") is not null)
            return new PropRequest(PropMode.PropName, []);

        var prop = root.Element(Dav + "prop");
        if (prop is null)
            return new PropRequest(PropMode.AllProp, []);

        var names = prop.Elements().Select(e => e.Name).ToList();
        return new PropRequest(PropMode.Named, names);
    }

    /// <summary>Serializes a <c>207 Multi-Status</c> for the given resources.</summary>
    public static string WriteMultiStatus(IEnumerable<WebDavEntry> entries, PropRequest request)
    {
        var multistatus = new XElement(Dav + "multistatus",
            new XAttribute(XNamespace.Xmlns + "D", Dav.NamespaceName));

        foreach (var entry in entries)
            multistatus.Add(BuildResponse(entry, request));

        return Serialize(multistatus);
    }

    private static XElement BuildResponse(WebDavEntry entry, PropRequest request)
    {
        var found = new List<XElement>();
        var notFound = new List<XElement>();

        void Emit(XName name, Func<XElement?> build)
        {
            var element = build();
            if (element is not null) found.Add(element);
            else notFound.Add(new XElement(name));
        }

        var wanted = ResolveWanted(request, entry);
        bool namesOnly = request.Mode == PropMode.PropName;

        foreach (var name in wanted)
        {
            if (namesOnly)
            {
                found.Add(new XElement(name));
                continue;
            }

            if (name == Dav + "resourcetype")
                found.Add(new XElement(name, entry.IsCollection ? new XElement(Dav + "collection") : null));
            else if (name == Dav + "displayname")
                found.Add(new XElement(name, entry.DisplayName));
            else if (name == Dav + "getlastmodified")
                found.Add(new XElement(name, entry.LastModifiedUtc.ToUniversalTime().ToString("R", CultureInfo.InvariantCulture)));
            else if (name == Dav + "creationdate")
                found.Add(new XElement(name, entry.CreationTimeUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)));
            else if (name == Dav + "getcontentlength")
                Emit(name, () => entry.IsCollection ? null : new XElement(name, entry.ContentLength));
            else if (name == Dav + "getcontenttype")
                Emit(name, () => entry.IsCollection ? null : new XElement(name, "application/octet-stream"));
            else if (name == Dav + "getetag")
                Emit(name, () => entry.IsCollection || entry.ETag is null ? null : new XElement(name, entry.ETag));
            else if (name == Dav + "supportedlock")
                found.Add(BuildSupportedLock());
            else if (name == Dav + "lockdiscovery")
                found.Add(BuildLockDiscovery(entry.Locks));
            else
                notFound.Add(new XElement(name));
        }

        var response = new XElement(Dav + "response",
            new XElement(Dav + "href", entry.Href));

        if (found.Count > 0)
            response.Add(Propstat(found, "HTTP/1.1 200 OK"));
        if (notFound.Count > 0)
            response.Add(Propstat(notFound, "HTTP/1.1 404 Not Found"));

        return response;
    }

    private static IEnumerable<XName> ResolveWanted(PropRequest request, WebDavEntry entry)
        => request.Mode == PropMode.Named ? request.Named : SupportedProps;

    private static XElement Propstat(IEnumerable<XElement> props, string status)
        => new(Dav + "propstat",
            new XElement(Dav + "prop", props),
            new XElement(Dav + "status", status));

    private static XElement BuildSupportedLock()
        => new(Dav + "supportedlock",
            new XElement(Dav + "lockentry",
                new XElement(Dav + "lockscope", new XElement(Dav + "exclusive")),
                new XElement(Dav + "locktype", new XElement(Dav + "write"))));

    private static XElement BuildLockDiscovery(IReadOnlyList<WebDavActiveLock> locks)
    {
        var discovery = new XElement(Dav + "lockdiscovery");
        foreach (var active in locks)
        {
            discovery.Add(new XElement(Dav + "activelock",
                new XElement(Dav + "locktype", new XElement(Dav + "write")),
                new XElement(Dav + "lockscope", new XElement(Dav + "exclusive")),
                new XElement(Dav + "depth", active.DepthInfinity ? "infinity" : "0"),
                active.OwnerRaw is null ? null : new XElement(Dav + "owner", active.OwnerRaw),
                new XElement(Dav + "timeout", $"Second-{active.RemainingSeconds}"),
                new XElement(Dav + "locktoken",
                    new XElement(Dav + "href", active.Token))));
        }
        return discovery;
    }

    private static string Serialize(XElement root) => WebDavXml.Serialize(root);
}

/// <summary>
/// Serializes WebDAV XML with a prolog that actually matches the bytes sent on the
/// wire. A plain <see cref="StringWriter"/> reports UTF-16 as its encoding, and
/// <see cref="XDocument.Save(TextWriter)"/> then stamps <c>encoding="utf-16"</c>
/// into the declaration even though the HTTP response body is UTF-8 — a mismatch
/// that strict clients (Windows Explorer, Finder) reject. Forcing the writer to
/// report UTF-8 makes the declaration agree with the response.
/// </summary>
internal static class WebDavXml
{
    private sealed class Utf8StringWriter : StringWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
    }

    public static string Serialize(XElement root)
    {
        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        using var writer = new Utf8StringWriter();
        doc.Save(writer, SaveOptions.DisableFormatting);
        return writer.ToString();
    }
}
