using Kaimo_File_Server.Web.Helpers;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.Helpers;

/// <summary>
/// The editable path bar shows "Root/sub/folder"; whatever the user types or pastes must map
/// back to a clean path below that root, and never climb above it.
/// </summary>
public class PathBarTextTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("Docs", "")]
    [InlineData("docs", "")]
    [InlineData("Docs/a/b", "a/b")]
    [InlineData("DOCS/a", "a")]
    [InlineData("/Docs/a/", "a")]
    [InlineData("  Docs/a  ", "a")]
    [InlineData(@"Docs\a\b", "a/b")]
    [InlineData(@"\Docs\a", "a")]
    [InlineData("Docs//a///b", "a/b")]
    [InlineData("a/b", "a/b")]
    [InlineData("/a/b", "a/b")]
    [InlineData("Docsx/a", "Docsx/a")]
    [InlineData("Docs/a/./b", "a/b")]
    [InlineData("Docs/a/../b", "b")]
    [InlineData("Docs/../x", "x")]
    [InlineData("../../x", "x")]
    [InlineData("Docs/a/b/..", "a")]
    [InlineData("Docs/report..final", "report..final")]
    // Paths copied from Windows Explorer / SMB carry the server name in front of the share.
    [InlineData(@"\\kaimo\Docs\Reports", "Reports")]
    [InlineData(@"\\kaimo\Docs", "")]
    [InlineData("//kaimo/Docs/a/b", "a/b")]
    // The bar always shows the root first, so a leading root name is the root, even when a
    // subfolder of the same name exists (reach it as "Docs/Docs").
    [InlineData("Docs/Docs/x", "Docs/x")]
    [InlineData("\"Docs\\a\\b\"", "a/b")]
    [InlineData("\"a\"", "a")]
    public void ToRelative_StripsRootAndNormalizes(string input, string expected)
        => Assert.Equal(expected, PathBarText.ToRelative(input, "Docs"));

    [Fact]
    public void ToRelative_RootNameWithSpaces()
        => Assert.Equal("x", PathBarText.ToRelative("My Share/x", "My Share"));

    [Theory]
    [InlineData("", "Docs")]
    [InlineData("a/b", "Docs/a/b")]
    public void Format_PrefixesRoot(string relative, string expected)
        => Assert.Equal(expected, PathBarText.Format("Docs", relative));

    // Absolute mode (file browser): "/" is the shares overview, "/Share/sub" a share path.

    [Theory]
    [InlineData("", "", "/Docs")]
    [InlineData("", "a/b", "/Docs/a/b")]
    [InlineData("Host/parent", "", "/Host/parent/Docs")]
    [InlineData("Host/parent", "x", "/Host/parent/Docs/x")]
    public void FormatAbsolute_StartsAtTheOverview(string prefix, string relative, string expected)
        => Assert.Equal(expected, PathBarText.FormatAbsolute(prefix, "Docs", relative));

    [Theory]
    [InlineData("/", "")]
    [InlineData("  /  ", "")]
    [InlineData(@"\", "")]
    [InlineData("/Other", "Other")]
    [InlineData("/Other/a/./b/", "Other/a/b")]
    [InlineData("/Other/../Docs/a", "Docs/a")]
    [InlineData("/..", "")]
    [InlineData(@"\Other\a", "Other/a")]
    // A UNC path names the share after the server, so it is absolute too.
    [InlineData(@"\\kaimo\Other\a", "Other/a")]
    [InlineData("//kaimo", "")]
    // Explorer's "Copy as path" wraps the path in double quotes.
    [InlineData("\"\\\\127.0.0.1\\files\\ffff\\1. AT Pneumatik\\Textdokument (neu).txt\"", "files/ffff/1. AT Pneumatik/Textdokument (neu).txt")]
    [InlineData("\\\\127.0.0.1\\files\\ffff\\1. AT Pneumatik\\Textdokument (neu).txt", "files/ffff/1. AT Pneumatik/Textdokument (neu).txt")]
    [InlineData("\"\\\\127.0.0.1\\files\\ffff\\1. AT Pneumatik\\\"", "files/ffff/1. AT Pneumatik")]
    [InlineData("  \"/Other/a\"  ", "Other/a")]
    public void ToAbsolute_LeadingSlashAddressesTheOverview(string input, string expected)
        => Assert.Equal(expected, PathBarText.ToAbsolute(input));

    [Theory]
    [InlineData("")]
    [InlineData("Docs/a")]
    [InlineData("a")]
    public void ToAbsolute_RelativeInputIsNotAbsolute(string input)
        => Assert.Null(PathBarText.ToAbsolute(input));

    [Theory]
    [InlineData("Docs", "", "")]
    [InlineData("docs/a/b", "", "a/b")]
    [InlineData("Host/parent/Docs/x", "Host/parent", "x")]
    [InlineData("HOST/Parent/docs", "Host/parent", "")]
    public void BelowRoot_StripsPrefixAndRoot(string absolute, string prefix, string expected)
        => Assert.Equal(expected, PathBarText.BelowRoot(absolute, prefix, "Docs"));

    [Theory]
    [InlineData("", "")]
    [InlineData("Other/a", "")]
    [InlineData("Docsx", "")]
    [InlineData("Docs/a", "Host")]
    [InlineData("Host", "Host")]
    public void BelowRoot_ElsewhereIsNull(string absolute, string prefix)
        => Assert.Null(PathBarText.BelowRoot(absolute, prefix, "Docs"));
}
