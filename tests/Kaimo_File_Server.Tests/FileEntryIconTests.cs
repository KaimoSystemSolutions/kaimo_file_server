using Kaimo_File_Server.Web.Helpers;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// The one icon mapping the file browser and the global search share, so a folder, file or
/// recycle bin looks the same in both.
/// </summary>
public sealed class FileEntryIconTests
{
    [Fact]
    public void Folder_UsesTheFolderColors()
        => Assert.Equal(("folder.svg", "var(--folder-stroke)", "var(--folder-fill)"),
            FileHelper.GetEntryIcon("Reports", isDirectory: true, isRecycleBinRoot: false));

    [Fact]
    public void RecycleBinRoot_UsesTheTrashIcon()
        => Assert.Equal(("trash.svg", "currentColor", "none"),
            FileHelper.GetEntryIcon(".RECYCLE_BIN", isDirectory: true, isRecycleBinRoot: true));

    [Theory]
    [InlineData("report.pdf")]
    [InlineData("README")]
    public void File_UsesItsTypeIconInTertiaryText(string name)
        => Assert.Equal((FileHelper.GetIcon(name), "var(--text-tertiary)", "none"),
            FileHelper.GetEntryIcon(name, isDirectory: false, isRecycleBinRoot: false));
}
