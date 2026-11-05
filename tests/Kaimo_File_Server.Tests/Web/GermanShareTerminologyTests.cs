using System.Text.RegularExpressions;
using System.Xml.Linq;
using Kaimo_File_Server.Core.Services.Notifications;
using Kaimo_File_Server.Infrastructure.Notifications;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// The German UI calls a file-server share "Share" (der Share), never "Freigabe". The only
/// exception is Microsoft's own protocol name "Windows-Dateifreigabe" for SMB.
/// </summary>
public sealed class GermanShareTerminologyTests
{
    private static readonly Regex Freigabe = new("[Ff]reigabe(?<!Dateifreigabe)", RegexOptions.Compiled);

    [Fact]
    public void GermanResources_SayShareInsteadOfFreigabe()
    {
        var path = Path.Combine(FindRepoRoot(), "src/Kaimo_File_Server.Core/Language/Resources.de.resx");
        var offending = XDocument.Load(path).Root!.Elements("data")
            .Where(e => Freigabe.IsMatch((string?)e.Element("value") ?? ""))
            .Select(e => (string)e.Attribute("name")!)
            .ToList();

        Assert.Empty(offending);
    }

    [Fact]
    public void GermanShareLinkMail_SaysShareLink()
    {
        var template = DefaultMailTemplates.Get(NotificationEventCatalog.LinkAccessed, "de");

        Assert.DoesNotMatch(Freigabe, template.Subject);
        Assert.DoesNotMatch(Freigabe, template.Html);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Kaimo_File_Server.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
