using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Storage;
using Kaimo_File_Server.Search;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class FileServiceFolderSnapshotTests
{
    private readonly Guid _shareId = Guid.NewGuid();
    private readonly UserContext _user;
    private readonly Mock<IStorageEngine> _storage = new();
    private readonly Mock<IAclService> _acl = new();
    private readonly Mock<IFileVersionService> _versions = new();
    private readonly FileService _sut;

    public FileServiceFolderSnapshotTests()
    {
        var user = new User(Guid.NewGuid(), "Alice", "alice", "hash", "nt");
        _user = new UserContext(user, [], [], []);
        _sut = new FileService(
            _storage.Object, _acl.Object, Mock.Of<ISearchService>(),
            _shareId, _versions.Object);
    }

    [Fact]
    public async Task GetFolderSnapshot_InheritedFolderAllowAndExplicitChildDeny_FiltersChild()
    {
        DateTime at = DateTime.UtcNow;
        FileVersion visible = Version("docs/visible.txt", at);
        FileVersion denied = Version("docs/denied.txt", at);
        _acl.Setup(a => a.HasAccessAsync(
                _user, _shareId, "docs", true,
                FilePermission.ListReadData))
            .ReturnsAsync(true);
        _versions.Setup(v => v.GetFolderSnapshotAsync(_shareId, "docs", at))
            .ReturnsAsync([visible, denied]);
        _acl.Setup(a => a.HasAccessBatchAsync(
                _user, _shareId,
                It.IsAny<IReadOnlyList<(string relativePath, bool isDirectory)>>(),
                FilePermission.ListReadData))
            .ReturnsAsync(new Dictionary<string, bool>
            {
                [visible.FilePath] = true,
                [denied.FilePath] = false
            });

        var result = await _sut.GetFolderSnapshotAsync("docs", at, _user);

        Assert.Collection(result, item => Assert.Equal(visible.FilePath, item.FilePath));
        _acl.Verify(a => a.HasAccessBatchAsync(
            _user, _shareId,
            It.Is<IReadOnlyList<(string relativePath, bool isDirectory)>>(items =>
                items.All(item => !item.isDirectory)),
            FilePermission.ListReadData), Times.Once);
    }

    [Fact]
    public async Task GetFolderSnapshot_ChildAllowButParentRestricted_DeniesBeforeVersionLookup()
    {
        DateTime at = DateTime.UtcNow;
        _acl.Setup(a => a.HasAccessAsync(
                _user, _shareId, "restricted", true,
                FilePermission.ListReadData))
            .ReturnsAsync(false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.GetFolderSnapshotAsync("restricted", at, _user));

        _versions.Verify(v => v.GetFolderSnapshotAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>()),
            Times.Never);
        _acl.Verify(a => a.HasAccessBatchAsync(
            It.IsAny<UserContext>(), It.IsAny<Guid>(),
            It.IsAny<IReadOnlyList<(string relativePath, bool isDirectory)>>(),
            It.IsAny<FilePermission>()), Times.Never);
    }

    private FileVersion Version(string path, DateTime at) =>
        new(_shareId, path, at, "blob", "hash", 4, null, 1);
}
