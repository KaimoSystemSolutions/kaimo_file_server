using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Storage;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Core.Tests.Services.Files;

/// <summary>Virtual-share mount points reserve their name and follow parent renames.</summary>
public sealed class FileServiceMountReservationTests
{
    private const string Mount = "Team/Cloud";
    private readonly Mock<IStorageEngine> _storage = new();
    private readonly Mock<IAclService> _acl = new();
    private readonly Mock<ICloudAccessMountGuard> _guard = new();
    private readonly Guid _shareId = Guid.NewGuid();
    private readonly UserContext _user = new(new User(Guid.NewGuid(), "T", "t", "h", "nt"), [], [], []);
    private readonly FileService _sut;

    public FileServiceMountReservationTests()
    {
        _acl.Setup(a => a.HasAccessAsync(It.IsAny<UserContext>(), _shareId,
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<FilePermission>()))
            .ReturnsAsync(true);
        _guard.Setup(g => g.IsReservedAsync(_shareId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, string p, CancellationToken _) =>
                p.Equals(Mount, StringComparison.OrdinalIgnoreCase)
                || p.StartsWith(Mount + "/", StringComparison.OrdinalIgnoreCase));
        _storage.Setup(s => s.ToAbsolutePath(It.IsAny<string>())).Returns<string>(p => "/data/" + p);
        _sut = new FileService(_storage.Object, _acl.Object, null, _shareId, mountGuard: _guard.Object);
    }

    public static TheoryData<string> ReservedTargets => new() { "Team/Cloud", "team/CLOUD", "Team/Cloud/x.txt" };

    [Theory, MemberData(nameof(ReservedTargets))]
    public async Task CreateDirectory_OnMount_IsRejected(string path)
        => await Assert.ThrowsAsync<MountPathReservedException>(() => _sut.CreateDirectoryAsync(path, _user));

    [Theory, MemberData(nameof(ReservedTargets))]
    public async Task CreateFile_OnMount_IsRejected(string path)
        => await Assert.ThrowsAsync<MountPathReservedException>(() => _sut.CreateFileAsync(path, _user));

    [Fact]
    public async Task WriteFile_OnMount_IsRejected()
        => await Assert.ThrowsAsync<MountPathReservedException>(
            () => _sut.WriteFileAsync("Team/Cloud", Stream.Null, _user));

    [Fact]
    public async Task Rename_OntoMount_IsRejected()
    {
        _storage.Setup(s => s.IsDirectoryAsync("Team/Local")).ReturnsAsync(true);
        await Assert.ThrowsAsync<MountPathReservedException>(
            () => _sut.RenameAsync("Team/Local", "Team/Cloud", _user));
        _storage.Verify(s => s.RenameDirectoryAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Unzip_And_Archive_OntoMount_AreRejected()
    {
        await Assert.ThrowsAsync<MountPathReservedException>(() => _sut.UnzipAsync("a.zip", "Team/Cloud", _user));
        await Assert.ThrowsAsync<MountPathReservedException>(
            () => _sut.ArchiveAsync(["a.txt"], "Team/Cloud", "zip", _user));
    }

    [Fact]
    public async Task OpenCreate_OnMount_IsRejected()
    {
        _storage.Setup(s => s.ExistsAsync("Team/Cloud")).ReturnsAsync(false);
        await Assert.ThrowsAsync<MountPathReservedException>(() => _sut.OpenAsync(
            "Team/Cloud", OpenMode.Create, AccessIntent.Write, ShareIntent.None, _user));
    }

    [Fact]
    public async Task CanCreate_OnMount_ReturnsFalse()
        => Assert.False(await _sut.CanCreateAsync("Team/Cloud", _user));

    [Fact]
    public async Task CreateDirectory_Sibling_IsAllowed()
    {
        await _sut.CreateDirectoryAsync("Team/Cloud2", _user);
        _storage.Verify(s => s.CreateDirectory("Team/Cloud2"), Times.Once);
    }

    [Fact]
    public async Task WithoutGuard_BehaviourIsUnchanged()
    {
        var sut = new FileService(_storage.Object, _acl.Object, null, _shareId);
        await sut.CreateDirectoryAsync("Team/Cloud", _user);
        _storage.Verify(s => s.CreateDirectory("Team/Cloud"), Times.Once);
    }

    [Fact]
    public async Task FolderRename_RebasesMounts()
    {
        _storage.Setup(s => s.IsDirectoryAsync("Team")).ReturnsAsync(true);
        await _sut.RenameAsync("Team", "Team2", _user);
        _guard.Verify(g => g.RenamePathAsync(_shareId, "Team", "Team2", 0), Times.Once);
    }

    [Fact]
    public async Task FileRename_DoesNotTouchMounts()
    {
        _storage.Setup(s => s.IsDirectoryAsync("a.txt")).ReturnsAsync(false);
        await _sut.RenameAsync("a.txt", "b.txt", _user);
        _guard.Verify(g => g.RenamePathAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task RecycleDelete_PassesRecyclePathToGuard()
    {
        _storage.Setup(s => s.IsDirectoryAsync("Team")).ReturnsAsync(true);
        _storage.Setup(s => s.MoveAsync("Team", ".RECYCLE_BIN/Team")).ReturnsAsync(".RECYCLE_BIN/Team");

        await _sut.DeleteFileAsync("Team", _user, isRecycleEnabled: true);

        _guard.Verify(g => g.RenamePathAsync(_shareId, "Team", ".RECYCLE_BIN/Team", 0), Times.Once);
    }

    [Fact]
    public async Task PermanentFolderDelete_Detaches_FileDeleteDoesNot()
    {
        _storage.Setup(s => s.IsDirectoryAsync("Team")).ReturnsAsync(true);
        _storage.Setup(s => s.IsDirectoryAsync("a.txt")).ReturnsAsync(false);

        await _sut.DeleteFileAsync("Team", _user, isRecycleEnabled: false);
        await _sut.DeleteFileAsync("a.txt", _user, isRecycleEnabled: false);

        _guard.Verify(g => g.DetachAsync(_shareId, "Team"), Times.Once);
        _guard.Verify(g => g.DetachAsync(_shareId, "a.txt"), Times.Never);
    }

    [Fact]
    public async Task ExternalSmbEvents_ReachTheGuard()
    {
        await _sut.NotifyExternalRenameAsync("Team", "Team2", isDirectory: true, Guid.NewGuid());
        await _sut.NotifyExternalDeleteAsync("Team2", isDirectory: true);

        _guard.Verify(g => g.RenamePathAsync(_shareId, "Team", "Team2", 0), Times.Once);
        _guard.Verify(g => g.DetachAsync(_shareId, "Team2"), Times.Once);
    }
}
