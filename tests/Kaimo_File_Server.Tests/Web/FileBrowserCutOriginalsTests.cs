using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Web.Components.ViewModels;
using Xunit;
using FileBrowserPage = Kaimo_File_Server.Web.Components.Pages.Files.FileBrowser.FileBrowser;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// The last phase of a cut within one share: once every copy exists, the originals are deleted
/// innermost first. An original that cannot be deleted must be reported, never swallowed, so
/// the paste does not claim a successful move while the item still exists twice.
/// </summary>
public sealed class FileBrowserCutOriginalsTests
{
    private static FileMetadata Item(string path)
        => new() { Path = path, Name = path[(path.LastIndexOf('/') + 1)..], Acl = [] };

    private static readonly List<FileMetadata> Items = [Item("src"), Item("src/a.txt"), Item("src/b.txt")];

    [Fact]
    public async Task AllDeleted_ReturnsNoFailures_AndDeletesInnermostFirst()
    {
        var deleted = new List<string>();

        var failed = await FileBrowserPage.DeleteCutOriginalsAsync(
            Items, item => { deleted.Add(item.Path); return Task.FromResult(OperationResult.Ok()); }, () => { });

        Assert.Empty(failed);
        Assert.Equal(["src/b.txt", "src/a.txt", "src"], deleted);
    }

    [Fact]
    public async Task FailedDelete_IsReported_AndTheRemainingOriginalsAreStillDeleted()
    {
        var deleted = new List<string>();

        var failed = await FileBrowserPage.DeleteCutOriginalsAsync(
            Items,
            item =>
            {
                deleted.Add(item.Path);
                return Task.FromResult(item.Path == "src/b.txt" ? OperationResult.Fail("locked") : OperationResult.Ok());
            },
            () => { });

        Assert.Equal(["b.txt"], failed);
        Assert.Equal(["src/b.txt", "src/a.txt", "src"], deleted);
    }

    [Fact]
    public async Task StopRequested_PropagatesBeforeTheNextDelete()
    {
        var deleted = new List<string>();

        await Assert.ThrowsAsync<OperationCanceledException>(() => FileBrowserPage.DeleteCutOriginalsAsync(
            Items,
            item => { deleted.Add(item.Path); return Task.FromResult(OperationResult.Ok()); },
            () => { if (deleted.Count == 1) throw new OperationCanceledException(); }));

        Assert.Equal(["src/b.txt"], deleted);
    }
}
