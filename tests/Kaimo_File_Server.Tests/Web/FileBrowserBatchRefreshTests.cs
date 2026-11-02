using Xunit;
using FileBrowserPage = Kaimo_File_Server.Web.Components.Pages.Files.FileBrowser.FileBrowser;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Batches finish in the background. Their closing refresh only reloads the folder they ran
/// in while it is still on screen; another folder the user opened meanwhile is left alone.
/// </summary>
public sealed class FileBrowserBatchRefreshTests
{
    [Theory]
    [InlineData("share", "docs", "share", "docs")]
    [InlineData("share", null, "share", "")]
    [InlineData("share", "/docs/", "share", "docs")]
    public void SameFolder_IsStillShown(string batchShare, string? batchPath, string shownShare, string? shownPath)
        => Assert.True(FileBrowserPage.IsSameLocation(batchShare, batchPath, shownShare, shownPath));

    [Theory]
    [InlineData("share", "docs", "share", "notes")]
    [InlineData("share", "docs", "other", "docs")]
    [InlineData("share", "docs", "share", "docs/sub")]
    public void OtherFolder_IsNotTheBatchFolder(string batchShare, string? batchPath, string shownShare, string? shownPath)
        => Assert.False(FileBrowserPage.IsSameLocation(batchShare, batchPath, shownShare, shownPath));
}
