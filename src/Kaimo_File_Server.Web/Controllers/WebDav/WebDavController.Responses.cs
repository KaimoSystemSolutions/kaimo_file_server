using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Web.Controllers.Api;
using Microsoft.AspNetCore.Mvc;

namespace Kaimo_File_Server.Web.Controllers.WebDav;

public partial class WebDavController
{
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
}
