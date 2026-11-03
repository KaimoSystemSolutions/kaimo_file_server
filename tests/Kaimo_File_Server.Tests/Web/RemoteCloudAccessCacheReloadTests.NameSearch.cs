using Kaimo_File_Server.Core.Services.ExternalStorage;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// The global search inside a virtual share walks the remote tree below a folder and matches
/// names only (no search index): case-insensitive substring, folders and files, bounded by a
/// hit cap and a time budget that returns what was found so far.
/// </summary>
public sealed partial class RemoteCloudAccessCacheReloadTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    private void SetupTree(params (string Folder, RemoteStorageItem[] Items)[] folders)
    {
        foreach (var (folder, items) in folders)
            _remote.Setup(x => x.ListAsync(folder, It.IsAny<CancellationToken>()))
                .ReturnsAsync(items);
    }

    private static RemoteStorageItem FileEntry(string path)
        => new(path[(path.LastIndexOf('/') + 1)..], path, false, 1, null);

    private static RemoteStorageItem DirEntry(string path)
        => new(path[(path.LastIndexOf('/') + 1)..], path, true, null, null);

    [Fact]
    public async Task SearchNames_FindsFilesAndFoldersBelowStartFolderOnly()
    {
        SetupTree(
            ("", [DirEntry("Docs"), FileEntry("report-root.txt")]),
            ("Docs", [DirEntry("Docs/Reports"), FileEntry("Docs/Report-a.txt"), FileEntry("Docs/other.txt")]),
            ("Docs/Reports", [FileEntry("Docs/Reports/annual.pdf"), FileEntry("Docs/Reports/deep-REPORT.md")]));
        var viewModel = NewViewModel();
        await viewModel.LoadShareAsync(_share.Id.ToString(), "Docs");

        var hits = await viewModel.SearchNamesAsync(_share.Id, "Docs", "report", 50, Budget, CancellationToken.None);

        Assert.Equal(
            ["Docs/Reports", "Docs/Report-a.txt", "Docs/Reports/deep-REPORT.md"],
            hits.Select(h => h.Path));
        Assert.True(hits[0].IsDirectory);
    }

    [Fact]
    public async Task SearchNames_StopsAtHitCap()
    {
        SetupTree(("", [FileEntry("a1.txt"), FileEntry("a2.txt"), FileEntry("a3.txt")]));
        var viewModel = NewViewModel();
        await viewModel.LoadShareAsync(_share.Id.ToString(), "");

        var hits = await viewModel.SearchNamesAsync(_share.Id, "", "a", 2, Budget, CancellationToken.None);

        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public async Task SearchNames_OtherThanLoadedShare_ReturnsNothing()
    {
        SetupTree(("", [FileEntry("match.txt")]));
        var viewModel = NewViewModel();
        await viewModel.LoadShareAsync(_share.Id.ToString(), "");

        var hits = await viewModel.SearchNamesAsync(Guid.NewGuid(), "", "match", 50, Budget, CancellationToken.None);

        Assert.Empty(hits);
    }

    [Fact]
    public async Task SearchNames_TraversalStartPath_ReturnsNothing()
    {
        SetupTree(("", [FileEntry("match.txt")]));
        var viewModel = NewViewModel();
        await viewModel.LoadShareAsync(_share.Id.ToString(), "");

        var hits = await viewModel.SearchNamesAsync(_share.Id, "../outside", "match", 50, Budget, CancellationToken.None);

        Assert.Empty(hits);
    }

    [Fact]
    public async Task SearchNames_FailingSubfolder_IsSkipped()
    {
        SetupTree(("", [DirEntry("Locked"), DirEntry("Open")]), ("Open", [FileEntry("Open/match.txt")]));
        _remote.Setup(x => x.ListAsync("Locked", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RemoteStorageAccessDeniedException("denied"));
        var viewModel = NewViewModel();
        await viewModel.LoadShareAsync(_share.Id.ToString(), "");

        var hits = await viewModel.SearchNamesAsync(_share.Id, "", "match", 50, Budget, CancellationToken.None);

        Assert.Equal(["Open/match.txt"], hits.Select(h => h.Path));
    }

    [Fact]
    public async Task SearchNames_BudgetExpired_ReturnsHitsFoundSoFar()
    {
        var never = new TaskCompletionSource<IReadOnlyList<RemoteStorageItem>>();
        SetupTree(("", [FileEntry("match-1.txt"), DirEntry("Slow")]));
        _remote.Setup(x => x.ListAsync("Slow", It.IsAny<CancellationToken>())).Returns(never.Task);
        var viewModel = NewViewModel();
        await viewModel.LoadShareAsync(_share.Id.ToString(), "");

        var hits = await viewModel.SearchNamesAsync(
            _share.Id, "", "match", 50, TimeSpan.FromMilliseconds(100), CancellationToken.None);

        Assert.Equal(["match-1.txt"], hits.Select(h => h.Path));
    }

    [Fact]
    public async Task SearchNames_CallerCancels_Throws()
    {
        var never = new TaskCompletionSource<IReadOnlyList<RemoteStorageItem>>();
        SetupTree(("", [DirEntry("Slow")]));
        _remote.Setup(x => x.ListAsync("Slow", It.IsAny<CancellationToken>())).Returns(never.Task);
        var viewModel = NewViewModel();
        await viewModel.LoadShareAsync(_share.Id.ToString(), "");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            viewModel.SearchNamesAsync(_share.Id, "", "x", 50, Budget, cts.Token));
    }
}
