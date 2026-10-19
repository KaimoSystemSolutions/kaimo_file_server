using Xunit;
using FileBrowserPage = Kaimo_File_Server.Web.Components.Pages.Files.FileBrowser.FileBrowser;

namespace Kaimo_File_Server.Tests;

public sealed class FileBrowserNavigationTests
{
    [Theory]
    [InlineData("", "a/b", "a")]
    [InlineData("a", "a/b/c", "b")]
    [InlineData("a/b", "a/b/c", "c")]
    [InlineData("/a/", "a/b", "b")]
    [InlineData("a", "a", null)]
    [InlineData("a", "ab/c", null)]
    [InlineData("a/b", "a", null)]
    [InlineData("a", "x/y", null)]
    [InlineData("", "", null)]
    public void ChildToward_ReturnsFirstSegmentBelowAncestor(string ancestor, string descendant, string? expected)
        => Assert.Equal(expected, FileBrowserPage.ChildToward(ancestor, descendant));
}
