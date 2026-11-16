using Kaimo_File_Server.Web.Helpers;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.Helpers;

/// <summary>
/// Folder names are placed into /files/... URLs; characters with URL meaning ('#', '?', '%')
/// must be escaped per segment so the router hands back the exact name.
/// </summary>
public class RouteSubPathTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("a/b", "a/b")]
    [InlineData("Q1 #draft", "Q1%20%23draft")]
    [InlineData("a?b/100%", "a%3Fb/100%25")]
    [InlineData("Übersicht", "%C3%9Cbersicht")]
    public void Escape_EscapesEachSegment(string subPath, string expected)
        => Assert.Equal(expected, RouteSubPath.Escape(subPath));
}
