using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Static guard against user-facing text that bypasses the resx files: literal text in Razor
/// markup, literal tooltip/placeholder/ARIA attributes, literal message assignments in Web C# code,
/// and key drift between the English and German resource files.
/// The scan is regex based and line oriented, so it misses text split across lines in unusual ways
/// and literals built inside @code blocks; it is a safety net, not a parser.
/// </summary>
public class HardcodedUiTextTests
{
    // Product, protocol and unit names that read the same in every language.
    private static readonly HashSet<string> LanguageNeutral = new(StringComparer.Ordinal)
    {
        "Kaimo File Server", "API", "WebDAV", "Elasticsearch", "STARTTLS", "SSL/TLS", "UTC", ".pem",
        "KB/s", "MB/s", "MB", "GB", "-----BEGIN CERTIFICATE-----",
    };

    // Reviewed false positives: "<file relative to Components>|<match>".
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        // MultiSelect.Label is a Func<T, string>; the value is a method group, not text.
        "Pages/Notifications/Notifications.razor|Label=\"PermissionLabel\"",
    };

    private static readonly Regex[] Unscanned =
    [
        new(@"@\*.*?\*@", RegexOptions.Singleline),
        new(@"<!--.*?-->", RegexOptions.Singleline),
        new(@"<(style|script)\b.*?</\1>", RegexOptions.Singleline),
        new(@"@(code|functions)\s*\{.*\z", RegexOptions.Singleline),
    ];

    // Characters that mark Razor/C# code (e.g. "a > 0 && a < b"), not prose.
    private const string NotText = @"<>@{}();=&|";
    private static readonly Regex InlineText = new($@">([^{NotText}]*\p{{L}}{{2}}[^{NotText}]*)<");
    private static readonly Regex BareTextLine = new(@"^\s*(\p{L}[^<>@{}();=:,&|]*\s[^<>@{}();=:,&|]*)$");
    private static readonly Regex CSharpLine = new(@"^\s*(else|case|return|var|break)\b|\w\.\w");
    private static readonly Regex TextAttribute = new(
        "\\b(?<name>title|placeholder|aria-label|alt|Label|Title|Placeholder|Text|Tooltip|Hint|Subtitle|Description|EmptyText|LinkLabel|AriaLabel|PlaceholderText|Error)=\"(?<value>[^\"@][^\"]*)\"");
    private static readonly Regex Words = new(@"\p{L}{2}");
    private static readonly Regex WordPair = new(@"\p{L}+ \p{L}+");

    private static readonly Regex MessageAssignment = new(
        "\\b\\w*(Message|Error|Warning|Success|Status|Title|Label|Text)\\s*=\\s*(?<interp>\\$?)\"(?<value>[^\"]*)\"");
    private static readonly Regex InterpolationHole = new(@"\{[^}]*\}");

    /// <summary>Returns "line: match" for every hard-coded UI text in one Razor file.</summary>
    internal static IEnumerable<string> ScanRazor(string source)
    {
        foreach (var region in Unscanned)
            source = region.Replace(source, m => new string('\n', m.Value.Count(c => c == '\n')));

        var lines = source.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var hits = InlineText.Matches(line).Select(m => m.Groups[1].Value.Trim()).ToList();

            var bare = BareTextLine.Match(line);
            if (bare.Success && !CSharpLine.IsMatch(line))
                hits.Add(bare.Groups[1].Value.Trim());

            foreach (Match m in TextAttribute.Matches(line))
            {
                var value = m.Groups["value"].Value;
                var technicalExample = m.Groups["name"].Value == "placeholder" && !WordPair.IsMatch(value);
                if (Words.IsMatch(value) && !technicalExample && !LanguageNeutral.Contains(value))
                    hits.Add(m.Value);
            }

            foreach (var hit in hits.Where(h => !LanguageNeutral.Contains(h)))
                yield return $"{i + 1}: {hit}";
        }
    }

    [Theory]
    [InlineData("<p>Lade Einstellungen…</p>")]
    [InlineData("<button title=\"Close\">x</button>")]
    [InlineData("<button aria-label=\"Zoom in\">+</button>")]
    [InlineData("<input placeholder=\"Search files\" />")]
    [InlineData("<InfoButton Text=\"Explains this\" />")]
    [InlineData("<p>\n    Some sentence of text\n</p>")]
    public void ScanRazor_FlagsHardcodedText(string razor) => Assert.NotEmpty(ScanRazor(razor));

    [Theory]
    [InlineData("<p>@Resources.Web_Common_Loading</p>")]
    [InlineData("<button title=\"@Resources.Web_Common_Close\">x</button>")]
    [InlineData("<input placeholder=\"smtp.example.com\" />")]
    [InlineData("<span>MB</span>")]
    [InlineData("@* <p>Commented out</p> *@")]
    [InlineData("@if (x)\n{\n    <p>@Text(\"Web_Key\", \"Fallback text\")</p>\n}\nelse\n{\n}")]
    [InlineData("@if (count > 0 && count < max) { }")]
    [InlineData("<p>@x</p>\n@code {\n    private string s = \"<b>Not markup</b>\";\n}")]
    public void ScanRazor_IgnoresLocalizedAndTechnicalText(string razor) => Assert.Empty(ScanRazor(razor));

    [Fact]
    public void RazorMarkup_HasNoHardcodedUiText()
    {
        var components = Path.Combine(WebProjectDir(), "Components");
        var offenders = Directory.EnumerateFiles(components, "*.razor", SearchOption.AllDirectories)
            .SelectMany(file =>
            {
                var rel = Path.GetRelativePath(components, file).Replace('\\', '/');
                return ScanRazor(File.ReadAllText(file))
                    .Where(hit => !Allowed.Contains($"{rel}|{hit[(hit.IndexOf(": ", StringComparison.Ordinal) + 2)..]}"))
                    .Select(hit => $"{rel}:{hit}");
            })
            .ToList();

        Assert.True(offenders.Count == 0,
            "Hard-coded UI text; move it to Resources.resx + Resources.de.resx (or allow-list it here with a reason):\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public void WebCode_AssignsNoHardcodedMessages()
    {
        var web = WebProjectDir();
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(web, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(web, file).Replace('\\', '/');
            if (rel.StartsWith("obj/", StringComparison.Ordinal) || rel.StartsWith("bin/", StringComparison.Ordinal)) continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                foreach (Match m in MessageAssignment.Matches(lines[i]))
                {
                    var value = m.Groups["value"].Value;
                    if (m.Groups["interp"].Value == "$") value = InterpolationHole.Replace(value, "");
                    if (Words.IsMatch(value)) offenders.Add($"{rel}:{i + 1}: {m.Value}");
                }
        }

        Assert.True(offenders.Count == 0, "Hard-coded user-facing message:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void LanguageFiles_DefineTheSameNonEmptyKeys()
    {
        var en = ResxValues("Resources.resx");
        var de = ResxValues("Resources.de.resx");

        Assert.Empty(en.Keys.Except(de.Keys).Select(k => "missing in de: " + k)
            .Concat(de.Keys.Except(en.Keys).Select(k => "missing in en: " + k))
            .Concat(en.Concat(de).Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => "empty: " + kv.Key)));
    }

    private static string WebProjectDir() => Directory.GetParent(MailNotificationTests.FindWebRoot())!.FullName;

    private static Dictionary<string, string> ResxValues(string file)
        => XDocument.Load(Path.Combine(WebProjectDir(), "..", "Kaimo_File_Server.Core", "Language", file))
            .Root!.Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!, e => (string?)e.Element("value") ?? "");
}
