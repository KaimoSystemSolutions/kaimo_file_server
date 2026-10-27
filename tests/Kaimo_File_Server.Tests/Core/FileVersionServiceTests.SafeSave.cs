using Kaimo_File_Server.Core.Services.File;
using Xunit;

namespace Kaimo_File_Server.Tests;

public partial class FileVersionServiceTests
{
    // ═══════════════════════════════════════════
    //  Safe-save editors keep the document's history
    // ═══════════════════════════════════════════

    private async Task SnapshotAsync(string path, string content)
    {
        await _sut.CreateVersionAsync(ShareA, path, ToStream(content));
        _time.Advance(TimeSpan.FromSeconds(2));
    }

    private async Task<string> ReadAsync(string path, DateTime timestamp)
    {
        await using var stream = await _sut.ReadVersionAsync(ShareA, path, timestamp);
        return await new StreamReader(stream).ReadToEndAsync();
    }

    [Fact]
    public async Task RenamePathAsync_TempFileRenamedOverDocument_KeepsTheDocumentHistory()
    {
        // LibreOffice: write lu123.tmp, then rename it over the document.
        await SnapshotAsync("report.odt", "v1");
        await SnapshotAsync("report.odt", "v2");
        await SnapshotAsync("lu123.tmp", "v3");

        await _sut.RenamePathAsync(ShareA, "lu123.tmp", "report.odt");

        var history = await _sut.GetVersionsAsync(ShareA, "report.odt");
        Assert.Equal([3, 2, 1], history.Select(v => v.VersionNumber));
        Assert.Equal("v3", await ReadAsync("report.odt", history[0].SnapshotTimestampUtc));
        Assert.Equal("v1", await ReadAsync("report.odt", history[2].SnapshotTimestampUtc));
        Assert.Empty(await _sut.GetVersionsAsync(ShareA, "lu123.tmp"));
    }

    [Fact]
    public async Task RenamePathAsync_WordSafeSave_KeepsTheDocumentHistory()
    {
        // Word: write ~WRD0000.tmp, move the document aside to ~WRL0001.tmp, move the new
        // content into place, delete the moved-aside original.
        await SnapshotAsync("report.docx", "v1");
        await SnapshotAsync("report.docx", "v2");
        await SnapshotAsync("~WRD0000.tmp", "v3");

        await _sut.RenamePathAsync(ShareA, "report.docx", "~WRL0001.tmp");
        await _sut.RenamePathAsync(ShareA, "~WRD0000.tmp", "report.docx");
        await _sut.DeletePathAsync(ShareA, "~WRL0001.tmp");

        var history = await _sut.GetVersionsAsync(ShareA, "report.docx");
        Assert.Equal([3, 2, 1], history.Select(v => v.VersionNumber));
        Assert.Equal("v3", await ReadAsync("report.docx", history[0].SnapshotTimestampUtc));
        Assert.Equal("v1", await ReadAsync("report.docx", history[2].SnapshotTimestampUtc));
    }

    [Fact]
    public async Task RenamePathAsync_RenameIntoVacatedNameAfterTheWindow_LeavesHistoryWithTheMovedFile()
    {
        await SnapshotAsync("report.docx", "old");
        await SnapshotAsync("draft.docx", "new");

        await _sut.RenamePathAsync(ShareA, "report.docx", "report-2025.docx");
        _time.Advance(RecentVersionRenames.Window + TimeSpan.FromSeconds(1));
        await _sut.RenamePathAsync(ShareA, "draft.docx", "report.docx");

        Assert.Single(await _sut.GetVersionsAsync(ShareA, "report-2025.docx"));
        Assert.Single(await _sut.GetVersionsAsync(ShareA, "report.docx"));
    }

    [Fact]
    public async Task RenamePathAsync_FolderMovedAsideAndReplaced_DoesNotPullTheFolderHistoryBack()
    {
        await SnapshotAsync("project/a.txt", "a");
        await SnapshotAsync("incoming/b.txt", "b");

        await _sut.RenamePathAsync(ShareA, "project", "project-old");
        await _sut.RenamePathAsync(ShareA, "incoming", "project");

        Assert.Single(await _sut.GetVersionsAsync(ShareA, "project-old/a.txt"));
        Assert.Empty(await _sut.GetVersionsAsync(ShareA, "project/a.txt"));
        Assert.Single(await _sut.GetVersionsAsync(ShareA, "project/b.txt"));
    }

    [Fact]
    public async Task RenamePathAsync_OlderFileOverNewerOne_NewestVersionIsTheLiveContent()
    {
        await SnapshotAsync("a.txt", "from a");
        await SnapshotAsync("b.txt", "from b");

        await _sut.RenamePathAsync(ShareA, "a.txt", "b.txt");

        var history = await _sut.GetVersionsAsync(ShareA, "b.txt");
        Assert.Equal(3, history.Count);
        Assert.Equal("from a", await ReadAsync("b.txt", history[0].SnapshotTimestampUtc));
        Assert.Equal("from b", await ReadAsync("b.txt", history[1].SnapshotTimestampUtc));
    }

    [Fact]
    public async Task RenamePathAsync_MergedHistory_SurvivesTheNextWrite()
    {
        await SnapshotAsync("report.odt", "v1");
        await SnapshotAsync("lu1.tmp", "v2");
        await _sut.RenamePathAsync(ShareA, "lu1.tmp", "report.odt");

        var next = await _sut.CreateVersionAsync(ShareA, "report.odt", ToStream("v3"));

        Assert.Equal(3, next!.VersionNumber);
        Assert.Equal(3, (await _sut.GetVersionsAsync(ShareA, "report.odt")).Count);
    }
}
