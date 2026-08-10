using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Helpers;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class FileBrowserViewModelContractTests
{
    [Fact]
    public void LocalCapabilities_ExposeExistingFileBrowserFeatures()
    {
        var capabilities = BrowserCapabilities.Local;

        Assert.True(capabilities.CanUpload);
        Assert.True(capabilities.CanMove);
        Assert.True(capabilities.CanArchive);
        Assert.True(capabilities.HasFileAcls);
        Assert.True(capabilities.HasVersions);
        Assert.True(capabilities.HasCloudSync);
        Assert.True(capabilities.HasSearchIntegration);
        Assert.True(capabilities.ShowDirectorySizes);
    }

    [Fact]
    public async Task RemoteBase_ProvidesNavigationWithoutLocalShareFeatures()
    {
        var viewModel = new TestRemoteFileBrowserViewModel();
        viewModel.Publish("projects/active");
        IFileBrowserViewModel browser = viewModel;

        Assert.Null(browser.CurrentShare);
        Assert.Equal(BrowserShareKind.Remote, browser.CurrentBrowserShare?.Kind);
        Assert.Equal("projects", browser.ParentPath);
        Assert.Equal(
            ["projects", "active"],
            browser.Breadcrumbs.Select(part => part.Name).ToArray());
        Assert.False(browser.Capabilities.HasFileAcls);
        Assert.False(browser.Capabilities.HasVersions);
        Assert.Null(browser.GetDirectorySize(browser.Items[0]));
        Assert.Empty(await browser.GetFileVersionsAsync(browser.Items[0]));
        Assert.Equal(0, browser.GetAclCount("projects/active"));
    }

    private sealed class TestRemoteFileBrowserViewModel : RemoteFileBrowserViewModelBase
    {
        public TestRemoteFileBrowserViewModel()
            : base(new BrowserCapabilities
            {
                CanOpen = true,
                CanUpload = true,
                CanCreateDirectory = true,
                CanRename = true,
                CanDelete = true,
                CanMove = true,
                CanCopy = true,
                HasProperties = true
            })
        {
        }

        public void Publish(string path)
            => CompleteLoad(
                new BrowserShareInfo(Guid.NewGuid(), "Remote", BrowserShareKind.Remote, "test"),
                path,
                [new FileMetadata { Name = "document.txt", Path = $"{path}/document.txt" }]);

        public override Task LoadShareAsync(string shareKey, string subPath = "")
            => Task.CompletedTask;

        public override Task RefreshCurrentDirectoryAsync() => Task.CompletedTask;

        public override Task<OperationResult> CreateFolderAsync(string folderName)
            => Task.FromResult(OperationResult.Ok());

        public override Task CreateFolderAtAsync(string path) => Task.CompletedTask;

        public override Task<OperationResult> DeleteAsync(FileMetadata item)
            => Task.FromResult(OperationResult.Ok());

        public override Task<OperationResult> RenameAsync(FileMetadata item, string newName)
            => Task.FromResult(OperationResult.Ok());

        public override Task<OperationResult> MoveAsync(FileMetadata item, string destinationPath)
            => Task.FromResult(OperationResult.Ok());

        public override Task<List<FileMetadata>> ListDirectoryAsync(string directoryPath)
            => Task.FromResult(new List<FileMetadata>());

        public override Task CopyAsync(
            FileMetadata item,
            string targetPath,
            CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override Task<(byte[] Data, string ContentType, PreviewKind Kind)?>
            ReadFileForPreviewAsync(FileMetadata file)
            => Task.FromResult<(byte[] Data, string ContentType, PreviewKind Kind)?>(null);

        public override Task<OperationResult> UploadFileAsync(
            string fileName,
            Stream fileStream,
            CancellationToken cancellationToken = default)
            => Task.FromResult(OperationResult.Ok());
    }
}
