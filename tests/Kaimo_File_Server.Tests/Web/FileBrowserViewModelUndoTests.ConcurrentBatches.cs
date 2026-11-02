using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Web.Components.ViewModels;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Batches no longer block the circuit, so the user keeps working while one runs. Undo keeps
/// a simple rule for that overlap: while a batch runs, undo is unavailable, and everything done
/// meanwhile is recorded into the batch's entry. Leaving the folder drops that entry. Paste work
/// runs off the dispatcher like deletes do.
/// </summary>
public sealed partial class FileBrowserViewModelUndoTests
{
    // Starts a batch that deletes a, then waits at the returned gate before deleting b.
    private (Task Batch, Task AtGate, TaskCompletionSource Gate) StartPausedBatch(
        string first, string second, FileOperationTarget? target = null)
    {
        var atGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task Batch()
        {
            using (_sut.BeginUndoGroup())
            {
                await _sut.DeleteAsync(Item(first), target ?? _sut.CaptureOperationTarget());
                atGate.SetResult();
                await gate.Task;
                await _sut.DeleteAsync(Item(second), target ?? _sut.CaptureOperationTarget());
            }
        }

        return (Batch(), atGate.Task, gate);
    }

    [Fact]
    public async Task PinnedDelete_AfterLeavingAndReturning_IsNotRecordedInTheNewVisit()
    {
        await _sut.LoadShareAsync("share", "docs");
        var target = _sut.CaptureOperationTarget();

        await _sut.LoadShareAsync("share", "notes");
        await _sut.LoadShareAsync("share", "docs");
        await _sut.DeleteAsync(Item("docs/a.txt"), target);

        Assert.False(_sut.CanUndo);
    }

    [Fact]
    public async Task BatchThatOutlivedALeaveAndReturn_IsNotUndonePiecemeal()
    {
        await _sut.LoadShareAsync("share", "docs");
        var (batch, atGate, gate) = StartPausedBatch("docs/a.txt", "docs/b.txt", _sut.CaptureOperationTarget());
        await atGate;

        await _sut.LoadShareAsync("share", "notes");
        await _sut.LoadShareAsync("share", "docs");
        gate.SetResult();
        await batch;

        Assert.False(_sut.CanUndo);
    }

    [Fact]
    public async Task OperationFromAnotherHandler_DuringABatch_IsUndoneTogetherWithIt()
    {
        await _sut.LoadShareAsync("share", "docs");
        var (batch, atGate, gate) = StartPausedBatch("docs/a.txt", "docs/b.txt");
        await atGate;

        await _sut.RenameAsync(Item("docs/x.txt"), "y.txt");
        Assert.False(_sut.CanUndo); // the batch is still running
        gate.SetResult();
        await batch;

        await _sut.UndoAsync();
        VerifyRenamed("docs/y.txt", "docs/x.txt", Times.Once());
        VerifyRenamed(".RECYCLE_BIN/docs/a.txt", "docs/a.txt", Times.Once());
        VerifyRenamed(".RECYCLE_BIN/docs/b.txt", "docs/b.txt", Times.Once());
        Assert.False(_sut.CanUndo);
    }

    [Fact]
    public async Task OverlappingBatches_UndoAsOneEntry_WhenTheLaterEndsFirst()
    {
        await _sut.LoadShareAsync("share", "docs");
        var (batch, atGate, gate) = StartPausedBatch("docs/a.txt", "docs/b.txt");
        await atGate;

        using (_sut.BeginUndoGroup())
            await _sut.DeleteAsync(Item("docs/c.txt"));
        Assert.False(_sut.CanUndo); // the first batch is still running
        gate.SetResult();
        await batch;

        await _sut.UndoAsync();
        VerifyRenamed(".RECYCLE_BIN/docs/a.txt", "docs/a.txt", Times.Once());
        VerifyRenamed(".RECYCLE_BIN/docs/b.txt", "docs/b.txt", Times.Once());
        VerifyRenamed(".RECYCLE_BIN/docs/c.txt", "docs/c.txt", Times.Once());
        Assert.False(_sut.CanUndo);
    }

    [Fact]
    public async Task BatchStillRunningAfterLeaving_DropsWhatTheNewFolderRecordedMeanwhile()
    {
        await _sut.LoadShareAsync("share", "docs");
        var (batch, atGate, gate) = StartPausedBatch("docs/a.txt", "docs/b.txt", _sut.CaptureOperationTarget());
        await atGate;

        await _sut.LoadShareAsync("share", "notes");
        await _sut.RenameAsync(Item("notes/x.txt"), "y.txt");
        Assert.False(_sut.CanUndo); // a batch is still running
        gate.SetResult();
        await batch;

        Assert.False(_sut.CanUndo);
    }

    [Fact]
    public async Task HistoryClearedDuringABatch_DropsEverythingRecordedUntilTheBatchEnds()
    {
        await _sut.LoadShareAsync("share", "docs");
        _fileService
            .Setup(s => s.DeleteFileReturningRecyclePathAsync("docs/gone.txt", It.IsAny<UserContext>(), true))
            .ReturnsAsync((string?)null);
        var (batch, atGate, gate) = StartPausedBatch("docs/a.txt", "docs/b.txt");
        await atGate;

        // Another handler clears the history (permanent delete), then renames.
        await _sut.DeleteAsync(Item("docs/gone.txt"));
        await _sut.RenameAsync(Item("docs/x.txt"), "y.txt");
        gate.SetResult();
        await batch;

        Assert.False(_sut.CanUndo);
    }

    [Fact]
    public async Task Rename_DoesNotBlockTheCaller_WhileTheStorageRenamesSynchronously()
    {
        await _sut.LoadShareAsync("share", "docs");
        using var storageBusy = new ManualResetEventSlim();
        _fileService
            .Setup(s => s.RenameAsync("docs/a.txt", "docs/b.txt", It.IsAny<UserContext>()))
            .Returns(() =>
            {
                storageBusy.Wait(TimeSpan.FromSeconds(5));
                return Task.CompletedTask;
            });

        var rename = _sut.RenameAsync(Item("docs/a.txt"), "b.txt");

        Assert.False(rename.IsCompleted);
        storageBusy.Set();
        Assert.True((await rename).Success);
    }

    [Fact]
    public async Task Copy_DisposesTheSourceStream()
    {
        await _sut.LoadShareAsync("share", "docs");
        var source = new MemoryStream([1, 2, 3]);
        _fileService
            .Setup(s => s.ReadFileAsync("src/a.txt", It.IsAny<UserContext>()))
            .ReturnsAsync(source);

        await _sut.CopyAsync(Item("src/a.txt"), "docs/a.txt", CancellationToken.None);

        Assert.False(source.CanRead);
    }

    [Fact]
    public async Task Copy_DoesNotBlockTheCaller_WhileTheStorageWritesSynchronously()
    {
        await _sut.LoadShareAsync("share", "docs");
        using var storageBusy = new ManualResetEventSlim();
        _fileService
            .Setup(s => s.WriteFileAsync("docs/a.txt", It.IsAny<Stream>(), It.IsAny<UserContext>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                storageBusy.Wait(TimeSpan.FromSeconds(5));
                return Task.CompletedTask;
            });

        var copy = _sut.CopyAsync(Item("src/a.txt"), "docs/a.txt", CancellationToken.None);

        Assert.False(copy.IsCompleted);
        storageBusy.Set();
        await copy;
    }

    [Fact]
    public async Task CreateFolderAt_DoesNotBlockTheCaller_WhileTheStorageCreatesSynchronously()
    {
        await _sut.LoadShareAsync("share", "docs");
        using var storageBusy = new ManualResetEventSlim();
        _fileService
            .Setup(s => s.CreateDirectoryAsync("docs/new", It.IsAny<UserContext>()))
            .Returns(() =>
            {
                storageBusy.Wait(TimeSpan.FromSeconds(5));
                return Task.CompletedTask;
            });

        var create = _sut.CreateFolderAtAsync("docs/new");

        Assert.False(create.IsCompleted);
        storageBusy.Set();
        await create;
    }
}
