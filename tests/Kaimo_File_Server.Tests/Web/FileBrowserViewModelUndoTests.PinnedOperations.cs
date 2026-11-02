using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Services.File;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Deletes run off the circuit dispatcher so browsing stays responsive, and a batch (delete,
/// paste, empty recycle bin) keeps working in the share it started in even when the user
/// opens another share meanwhile. Such a batch never touches the history of the new folder.
/// </summary>
public sealed partial class FileBrowserViewModelUndoTests
{
    private Mock<IFileService> UseOtherShareService()
    {
        var otherService = new Mock<IFileService>();
        _fileServiceFactory.Setup(f => f.CreateForShare(_otherShare)).Returns(otherService.Object);
        otherService
            .Setup(s => s.ListAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync([]);
        return otherService;
    }

    // ═══════════════════ Pinned paste ═══════════════════

    [Fact]
    public async Task PinnedCopy_ReadsAndWritesInTheShareItStartedIn_AfterNavigatingToAnotherShare()
    {
        var otherService = UseOtherShareService();
        await _sut.LoadShareAsync("share", "docs");
        var target = _sut.CaptureOperationTarget();

        await _sut.LoadShareAsync("other", "docs");
        await _sut.CopyAsync(Item("src/a.txt"), "docs/a.txt", CancellationToken.None, target);

        _fileService.Verify(s => s.ReadFileAsync("src/a.txt", It.IsAny<UserContext>()), Times.Once());
        _fileService.Verify(s => s.WriteFileAsync("docs/a.txt", It.IsAny<Stream>(), It.IsAny<UserContext>(), It.IsAny<CancellationToken>()), Times.Once());
        otherService.Verify(s => s.ReadFileAsync(It.IsAny<string>(), It.IsAny<UserContext>()), Times.Never());
    }

    [Fact]
    public async Task PinnedCreateFolder_CreatesInTheShareItStartedIn_AfterNavigatingToAnotherShare()
    {
        var otherService = UseOtherShareService();
        await _sut.LoadShareAsync("share", "docs");
        var target = _sut.CaptureOperationTarget();

        await _sut.LoadShareAsync("other", "docs");
        await _sut.CreateFolderAtAsync("docs/new", target);

        _fileService.Verify(s => s.CreateDirectoryAsync("docs/new", It.IsAny<UserContext>()), Times.Once());
        otherService.Verify(s => s.CreateDirectoryAsync(It.IsAny<string>(), It.IsAny<UserContext>()), Times.Never());
    }

    [Fact]
    public async Task PinnedList_ListsTheShareItStartedIn_AfterNavigatingToAnotherShare()
    {
        var otherService = UseOtherShareService();
        await _sut.LoadShareAsync("share", "docs");
        var target = _sut.CaptureOperationTarget();

        await _sut.LoadShareAsync("other", "docs");
        otherService.Invocations.Clear();
        await _sut.ListDirectoryAsync(".RECYCLE_BIN", target);

        _fileService.Verify(s => s.ListAsync(".RECYCLE_BIN", It.IsAny<UserContext>()), Times.Once());
        otherService.Verify(s => s.ListAsync(It.IsAny<string>(), It.IsAny<UserContext>()), Times.Never());
    }

    [Fact]
    public async Task PinnedOperations_WithoutALoadedShare_Throw()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.ListDirectoryAsync("docs", null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.CreateFolderAtAsync("docs/new", null));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.CopyAsync(Item("src/a.txt"), "docs/a.txt", CancellationToken.None, null));
    }

    // ═══════════════════ Pinning while the next folder loads ═══════════════════

    private void VerifyDeletedInShownShare(string path)
        => _fileService.Verify(
            s => s.DeleteFileReturningRecyclePathAsync(path, It.IsAny<UserContext>(), true), Times.Once());

    [Fact]
    public async Task BatchConfirmedWhileTheNextShareLoads_ActsOnTheShownShare()
    {
        await _sut.LoadShareAsync("share", "docs");
        var otherLookup = new TaskCompletionSource<ShareDefinition?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _shareRepo.Setup(r => r.GetByNameAsync("other")).Returns(otherLookup.Task);
        var load = _sut.LoadShareAsync("other", "docs");

        var result = await _sut.DeleteAsync(Item("docs/a.txt"), _sut.CaptureOperationTarget());

        Assert.True(result.Success);
        VerifyDeletedInShownShare("docs/a.txt");
        otherLookup.SetResult(_otherShare);
        await load;
    }

    [Fact]
    public async Task BatchConfirmedBeforeTheNextListingArrives_ActsOnTheShownShare()
    {
        var otherService = UseOtherShareService();
        var listing = new TaskCompletionSource<List<FileMetadata>>(TaskCreationOptions.RunContinuationsAsynchronously);
        otherService.Setup(s => s.ListAsync("docs", It.IsAny<UserContext>())).Returns(listing.Task);
        await _sut.LoadShareAsync("share", "docs");
        var load = _sut.LoadShareAsync("other", "docs");

        var result = await _sut.DeleteAsync(Item("docs/a.txt"), _sut.CaptureOperationTarget());

        Assert.True(result.Success);
        VerifyDeletedInShownShare("docs/a.txt");
        otherService.Verify(
            s => s.DeleteFileReturningRecyclePathAsync(It.IsAny<string>(), It.IsAny<UserContext>(), It.IsAny<bool>()),
            Times.Never());
        listing.SetResult([]);
        await load;
    }

    // ═══════════════════ A batch that outlived a navigation ═══════════════════

    [Fact]
    public async Task PinnedPermanentDelete_AfterNavigating_KeepsTheHistoryOfTheNewFolder()
    {
        await _sut.LoadShareAsync("share", "docs");
        var target = _sut.CaptureOperationTarget();
        _fileService
            .Setup(s => s.DeleteFileReturningRecyclePathAsync(".RECYCLE_BIN/old.txt", It.IsAny<UserContext>(), true))
            .ReturnsAsync((string?)null);

        await _sut.LoadShareAsync("share", "notes");
        await _sut.RenameAsync(Item("notes/a.txt"), "b.txt");
        await _sut.DeleteAsync(Item(".RECYCLE_BIN/old.txt"), target);

        Assert.True(_sut.CanUndo);
    }

    [Fact]
    public async Task PinnedDelete_AfterNavigating_IsNotRecordedInTheNewFolder()
    {
        await _sut.LoadShareAsync("share", "docs");
        var target = _sut.CaptureOperationTarget();

        await _sut.LoadShareAsync("share", "notes");
        await _sut.DeleteAsync(Item("docs/a.txt"), target);

        Assert.False(_sut.CanUndo);
    }

    [Fact]
    public async Task PinnedCopy_AfterNavigating_IsNotRecordedInTheNewFolder()
    {
        await _sut.LoadShareAsync("share", "docs");
        var target = _sut.CaptureOperationTarget();

        await _sut.LoadShareAsync("share", "notes");
        await _sut.CopyAsync(Item("src/a.txt"), "docs/a.txt", CancellationToken.None, target);
        await _sut.CreateFolderAtAsync("docs/new", target);

        Assert.False(_sut.CanUndo);
    }

    [Fact]
    public async Task GroupStartedAfterNavigating_WhileTheOldBatchStillRuns_IsNotUndoable()
    {
        await _sut.LoadShareAsync("share", "docs");
        var oldBatch = _sut.BeginUndoGroup();
        var oldTarget = _sut.CaptureOperationTarget();
        await _sut.DeleteAsync(Item("docs/a.txt"), oldTarget);

        await _sut.LoadShareAsync("share", "notes");
        using (_sut.BeginUndoGroup())
            await _sut.DeleteAsync(Item("notes/x.txt"));

        Assert.False(_sut.CanUndo);
        await _sut.DeleteAsync(Item("docs/b.txt"), oldTarget);
        oldBatch?.Dispose();

        Assert.False(_sut.CanUndo);
    }

    // ═══════════════════ Overlapping batches ═══════════════════

    [Fact]
    public async Task OverlappingGroups_UndoAsOneEntry_WhenTheFirstEndsFirst()
    {
        await _sut.LoadShareAsync("share", "docs");
        var first = _sut.BeginUndoGroup();
        await _sut.DeleteAsync(Item("docs/a.txt"));
        var second = _sut.BeginUndoGroup();
        await _sut.DeleteAsync(Item("docs/b.txt"));

        first?.Dispose();
        await _sut.DeleteAsync(Item("docs/c.txt"));
        Assert.False(_sut.CanUndo);
        second?.Dispose();

        await _sut.UndoAsync();
        VerifyRenamed(".RECYCLE_BIN/docs/a.txt", "docs/a.txt", Times.Once());
        VerifyRenamed(".RECYCLE_BIN/docs/b.txt", "docs/b.txt", Times.Once());
        VerifyRenamed(".RECYCLE_BIN/docs/c.txt", "docs/c.txt", Times.Once());
        Assert.False(_sut.CanUndo);
    }

    [Fact]
    public async Task DisposingAGroupTwice_DoesNotCloseAnOverlappingGroup()
    {
        await _sut.LoadShareAsync("share", "docs");
        var first = _sut.BeginUndoGroup();
        using (_sut.BeginUndoGroup())
        {
            first?.Dispose();
            first?.Dispose();
            await _sut.DeleteAsync(Item("docs/a.txt"));
            Assert.False(_sut.CanUndo);
        }

        Assert.True(_sut.CanUndo);
    }

    [Fact]
    public async Task Delete_DoesNotBlockTheCaller_WhileTheStorageDeletesSynchronously()
    {
        await _sut.LoadShareAsync("share", "docs");
        using var storageBusy = new ManualResetEventSlim();
        _fileService
            .Setup(s => s.DeleteFileReturningRecyclePathAsync("docs/big", It.IsAny<UserContext>(), true))
            .Returns(() =>
            {
                // Like Directory.Delete on a large folder: blocks the calling thread.
                storageBusy.Wait(TimeSpan.FromSeconds(5));
                return Task.FromResult<string?>(".RECYCLE_BIN/docs/big");
            });

        var delete = _sut.DeleteAsync(Item("docs/big", isDirectory: true));

        Assert.False(delete.IsCompleted);
        storageBusy.Set();
        Assert.True((await delete).Success);
    }

    [Fact]
    public async Task PinnedDelete_StaysInTheShareItStartedIn_AfterNavigatingToAnotherShare()
    {
        var otherService = new Mock<IFileService>();
        _fileServiceFactory.Setup(f => f.CreateForShare(_otherShare)).Returns(otherService.Object);
        otherService
            .Setup(s => s.ListAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync([]);
        await _sut.LoadShareAsync("share", "docs");
        var target = _sut.CaptureOperationTarget();

        await _sut.LoadShareAsync("other", "docs");
        var result = await _sut.DeleteAsync(Item("docs/a.txt"), target);

        Assert.True(result.Success);
        _fileService.Verify(s => s.DeleteFileReturningRecyclePathAsync("docs/a.txt", It.IsAny<UserContext>(), true), Times.Once());
        otherService.Verify(s => s.DeleteFileReturningRecyclePathAsync(
            It.IsAny<string>(), It.IsAny<UserContext>(), It.IsAny<bool>()), Times.Never());
    }

    [Fact]
    public async Task PinnedDelete_UsesTheRecycleSettingOfTheShareItStartedIn()
    {
        _otherShare.IsRecycleEnabled = false;
        await _sut.LoadShareAsync("share", "docs");
        var target = _sut.CaptureOperationTarget();

        await _sut.LoadShareAsync("other", "docs");
        await _sut.DeleteAsync(Item("docs/a.txt"), target);

        _fileService.Verify(s => s.DeleteFileReturningRecyclePathAsync("docs/a.txt", It.IsAny<UserContext>(), true), Times.Once());
    }

    [Fact]
    public async Task CaptureOperationTarget_IsNull_WithoutALoadedShare_AndDeleteFails()
    {
        var target = _sut.CaptureOperationTarget();

        var result = await _sut.DeleteAsync(Item("docs/a.txt"), target);

        Assert.Null(target);
        Assert.False(result.Success);
    }

    // ═══════════════════ Pinned move (drag & drop) ═══════════════════

    [Fact]
    public async Task PinnedMove_StaysInTheShareItStartedIn_AfterNavigatingToAnotherShare()
    {
        var otherService = UseOtherShareService();
        await _sut.LoadShareAsync("share", "docs");
        var target = _sut.CaptureOperationTarget();

        await _sut.LoadShareAsync("other", "docs");
        var result = await _sut.MoveAsync(Item("docs/a.txt"), "archive", target);

        Assert.True(result.Success);
        VerifyRenamed("docs/a.txt", "archive/a.txt", Times.Once());
        otherService.Verify(s => s.RenameAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<UserContext>()), Times.Never());
    }

    [Fact]
    public async Task PinnedMove_AfterNavigating_IsNotRecordedInTheNewFolder()
    {
        await _sut.LoadShareAsync("share", "docs");
        var target = _sut.CaptureOperationTarget();

        await _sut.LoadShareAsync("share", "notes");
        await _sut.MoveAsync(Item("docs/a.txt"), "archive", target);

        Assert.False(_sut.CanUndo);
    }

    [Fact]
    public async Task PinnedMove_InTheSameVisit_IsUndoable()
    {
        await _sut.LoadShareAsync("share", "docs");

        await _sut.MoveAsync(Item("docs/a.txt"), "archive", _sut.CaptureOperationTarget());

        Assert.True(_sut.CanUndo);
        await _sut.UndoAsync();
        VerifyRenamed("archive/a.txt", "docs/a.txt", Times.Once());
    }

    [Fact]
    public async Task PinnedMove_WithoutATarget_Fails()
    {
        var result = await _sut.MoveAsync(Item("docs/a.txt"), "archive", null);

        Assert.False(result.Success);
    }
}
