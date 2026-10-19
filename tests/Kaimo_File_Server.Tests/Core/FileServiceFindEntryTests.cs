using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Storage;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// <see cref="IFileService.FindEntryAsync"/> answers "does this item exist for this user" exactly
/// like a lookup in the parent listing would — same visibility, ACL and error rules — but
/// without listing the parent, so a WebDAV PUT into a folder of n files no longer costs O(n).
/// </summary>
public sealed class FileServiceFindEntryTests
{
    private readonly Mock<IStorageEngine> _storage = new();
    private readonly Mock<IAclService> _acl = new();
    private readonly Guid _shareId = Guid.NewGuid();
    private readonly FileService _sut;
    private readonly UserContext _user = new(new User(Guid.NewGuid(), "Test", "test", "hash", "nt"), [], [], []);

    public FileServiceFindEntryTests()
    {
        _sut = new FileService(_storage.Object, _acl.Object, null, _shareId);
        _storage.Setup(s => s.ExistsAsync("dir")).ReturnsAsync(true);
        _storage.Setup(s => s.IsDirectoryAsync("dir")).ReturnsAsync(true);
        AllowList("dir", true);
    }

    private void AllowList(string dir, bool allowed)
        => _acl.Setup(a => a.HasAccessAsync(_user, _shareId, dir, true, FilePermission.ListReadData))
            .ReturnsAsync(allowed);

    private void AllowItem(string path, bool allowed)
        => _acl.Setup(a => a.HasAccessBatchAsync(
                _user, _shareId, It.Is<IReadOnlyList<(string, bool)>>(l => l.Count == 1 && l[0].Item1 == path),
                FilePermission.ListReadData))
            .ReturnsAsync(new Dictionary<string, bool> { [path] = allowed });

    private void FileExists(string path, long size = 7)
    {
        _storage.Setup(s => s.ExistsAsync(path)).ReturnsAsync(true);
        _storage.Setup(s => s.IsDirectoryAsync(path)).ReturnsAsync(false);
        _storage.Setup(s => s.GetMetadataAsync(path)).ReturnsAsync(new FileMetadata
        {
            Path = path, Name = Path.GetFileName(path), Size = size, IsDirectory = false
        });
    }

    [Fact]
    public async Task ExistingReadableFile_IsReturned_WithoutListingTheParent()
    {
        FileExists("dir/a.txt");
        AllowItem("dir/a.txt", true);

        var entry = await _sut.FindEntryAsync("dir/a.txt", _user);

        Assert.Equal("a.txt", entry?.Name);
        Assert.Equal(7, entry?.Size);
        _storage.Verify(s => s.ListAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task MissingItem_IsNull()
    {
        _storage.Setup(s => s.ExistsAsync("dir/none.txt")).ReturnsAsync(false);
        AllowItem("dir/none.txt", true);

        Assert.Null(await _sut.FindEntryAsync("dir/none.txt", _user));
    }

    [Fact]
    public async Task MissingParent_IsNull()
    {
        AllowList("gone", true);
        _storage.Setup(s => s.ExistsAsync("gone")).ReturnsAsync(false);

        Assert.Null(await _sut.FindEntryAsync("gone/a.txt", _user));
    }

    [Fact]
    public async Task ItemTheUserMayNotRead_IsNull_LikeInTheListing()
    {
        FileExists("dir/secret.txt");
        AllowItem("dir/secret.txt", false);

        Assert.Null(await _sut.FindEntryAsync("dir/secret.txt", _user));
    }

    [Fact]
    public async Task ParentTheUserMayNotList_Throws_LikeTheListing()
    {
        AllowList("dir", false);
        FileExists("dir/a.txt");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _sut.FindEntryAsync("dir/a.txt", _user));
    }

    [Fact]
    public async Task InternalEntry_IsNull_LikeInTheListing()
    {
        AllowList("", true);
        _storage.Setup(s => s.ExistsAsync("")).ReturnsAsync(true);
        _storage.Setup(s => s.IsDirectoryAsync("")).ReturnsAsync(true);
        FileExists(".kaimo-close-captures");
        AllowItem(".kaimo-close-captures", true);

        Assert.Null(await _sut.FindEntryAsync(".kaimo-close-captures", _user));
    }

    [Fact]
    public async Task ShareRoot_IsItsOwnMetadata()
    {
        _storage.Setup(s => s.GetMetadataAsync("")).ReturnsAsync(new FileMetadata { Path = "", Name = "", IsDirectory = true });
        _acl.Setup(a => a.HasAccessAsync(_user, _shareId, "", true, FilePermission.ListReadData)).ReturnsAsync(true);

        Assert.True((await _sut.FindEntryAsync("", _user))?.IsDirectory);
    }
}
