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
}
