using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Web.Components.ViewModels;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.ViewModels.Files;

/// <summary>
/// A batch in a virtual share keeps working in the share it started in, even when the user
/// opens another virtual share meanwhile — also while that share is still loading, when the
/// browser already holds the new share but still shows the old listing.
/// </summary>
public sealed partial class RemoteCloudAccessCacheReloadTests
{
    private CloudAccessShare OtherShare() => new()
    {
        ConnectionId = _connection.Id, Name = "Other", RemoteRootPath = "other-root"
    };

    private static FileMetadata RemoteItem(string path)
        => new() { Path = path, Name = path[(path.LastIndexOf('/') + 1)..], Acl = [] };

    private void VerifyRemoteDeleted(string path, Times times)
        => _remote.Verify(x => x.DeleteAsync(path, false, It.IsAny<CancellationToken>()), times);

    [Fact]
    public async Task PinnedDelete_AfterOpeningAnotherShare_DeletesInTheShareItStartedIn()
    {
        var other = OtherShare();
        IFileBrowserViewModel viewModel = NewViewModel(other);
        await viewModel.LoadShareAsync(_share.Id.ToString(), "Docs");
        var target = viewModel.CaptureOperationTarget();

        await viewModel.LoadShareAsync(other.Id.ToString(), "Docs");
        var result = await viewModel.DeleteAsync(RemoteItem("Docs/x.txt"), target);

        Assert.True(result.Success);
        VerifyRemoteDeleted("Docs/x.txt", Times.Once());
        VerifyRemoteDeleted("other-root/Docs/x.txt", Times.Never());
    }

    [Fact]
    public async Task DeleteConfirmedWhileTheNextShareLoads_ActsOnTheShownShare()
    {
        var other = OtherShare();
        var listing = new TaskCompletionSource<IReadOnlyList<RemoteStorageItem>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _remote.Setup(x => x.ListAsync("other-root/Docs", It.IsAny<CancellationToken>())).Returns(listing.Task);
        IFileBrowserViewModel viewModel = NewViewModel(other);
        await viewModel.LoadShareAsync(_share.Id.ToString(), "Docs");
        var load = viewModel.LoadShareAsync(other.Id.ToString(), "Docs");

        var result = await viewModel.DeleteAsync(RemoteItem("Docs/x.txt"), viewModel.CaptureOperationTarget());

        Assert.True(result.Success);
        VerifyRemoteDeleted("Docs/x.txt", Times.Once());
        VerifyRemoteDeleted("other-root/Docs/x.txt", Times.Never());
        listing.SetResult([]);
        await load;
    }

    [Fact]
    public async Task PinnedCopyAndList_AfterOpeningAnotherShare_StayInTheShareTheyStartedIn()
    {
        var other = OtherShare();
        _remote.Setup(x => x.OpenReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream([1]));
        IFileBrowserViewModel viewModel = NewViewModel(other);
        await viewModel.LoadShareAsync(_share.Id.ToString(), "Docs");
        var target = viewModel.CaptureOperationTarget();

        await viewModel.LoadShareAsync(other.Id.ToString(), "Docs");
        await viewModel.ListDirectoryAsync("Src", target);
        await viewModel.CreateFolderAtAsync("Docs/New", target);
        await viewModel.CopyAsync(RemoteItem("Src/a.txt"), "Docs/a.txt", CancellationToken.None, target);

        Assert.Equal(1, _listCalls["Src"]);
        Assert.False(_listCalls.ContainsKey("other-root/Src"));
        _remote.Verify(x => x.CreateDirectoryAsync("Docs/New", It.IsAny<CancellationToken>()), Times.Once());
        _remote.Verify(x => x.OpenReadAsync("Src/a.txt", It.IsAny<CancellationToken>()), Times.Once());
        _remote.Verify(x => x.CreateDirectoryAsync(It.Is<string>(p => p.StartsWith("other-root")), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task PinnedMove_AfterOpeningAnotherShare_MovesInTheShareItStartedIn()
    {
        var other = OtherShare();
        IFileBrowserViewModel viewModel = NewViewModel(other);
        await viewModel.LoadShareAsync(_share.Id.ToString(), "Docs");
        var target = viewModel.CaptureOperationTarget();

        await viewModel.LoadShareAsync(other.Id.ToString(), "Docs");
        var result = await viewModel.MoveAsync(RemoteItem("Docs/x.txt"), "Archive", target);

        Assert.True(result.Success);
        _remote.Verify(x => x.MoveAsync("Docs/x.txt", "Archive/x.txt", It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task PinnedDelete_WithoutALoadedShare_Fails()
    {
        IFileBrowserViewModel viewModel = NewViewModel();

        Assert.Null(viewModel.CaptureOperationTarget());
        var result = await viewModel.DeleteAsync(RemoteItem("Docs/x.txt"), null);

        Assert.False(result.Success);
        _remote.Verify(x => x.DeleteAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never());
    }
}
