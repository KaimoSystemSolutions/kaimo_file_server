using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Storage;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Verifies that every <see cref="FileService"/> mutation path appends the right entry to the
/// per-share change log that backs the client <c>change_seq</c> feed. Storage and ACLs are mocked
/// (as in <c>FileServiceTests</c>); the change log is a capturing fake so we assert on what the
/// service recorded, not on a database.
/// </summary>
public sealed class FileServiceChangeLogTests
{
    private sealed class CapturingChangeLog : IFileChangeLog
    {
        public List<FileChangeLogEntry> Entries { get; } = new();

        public Task AppendAsync(
            Guid shareId, FileChangeType type, string path, bool isDirectory,
            string? oldPath = null, long? size = null, DateTime? modifiedAtUtc = null,
            CancellationToken ct = default)
        {
            Entries.Add(new FileChangeLogEntry
            {
                ShareId = shareId,
                ChangeType = type,
                Path = path,
                OldPath = oldPath,
                IsDirectory = isDirectory,
                Size = size,
                ModifiedAtUtc = modifiedAtUtc,
            });
            return Task.CompletedTask;
        }
    }

    private readonly Mock<IStorageEngine> _storage = new();
    private readonly Mock<IAclService> _acl = new();
    private readonly CapturingChangeLog _log = new();
    private readonly Guid _shareId = Guid.NewGuid();
    private readonly FileService _sut;

    public FileServiceChangeLogTests()
    {
        // Allow every permission; these tests are about the change-log side effect, not authz.
        _acl.Setup(a => a.HasAccessAsync(
                It.IsAny<UserContext>(), _shareId, It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<FilePermission>()))
            .ReturnsAsync(true);
        _storage.Setup(s => s.ToAbsolutePath(It.IsAny<string>())).Returns<string>(p => "/abs/" + p);

        _sut = new FileService(
            _storage.Object, _acl.Object, searchService: null, _shareId,
            versionService: null, ownershipService: null, logger: null,
            cloudSyncPathUpdater: null, cloudSyncOperations: null, changeLog: _log);
    }

    private static UserContext Ctx()
        => new(new User(Guid.NewGuid(), "Test", "test", "hash", "nt"), [], [], []);

    private FileChangeLogEntry Single()
        => Assert.Single(_log.Entries);

    [Fact]
    public async Task WriteFileAsync_NewFile_AppendsCreatedWithSize()
    {
        _storage.Setup(s => s.IsDirectoryAsync("docs/a.txt")).ReturnsAsync(false);
        _storage.Setup(s => s.ExistsAsync("docs/a.txt")).ReturnsAsync(false);
        var mtime = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        _storage.Setup(s => s.GetMetadataAsync("docs/a.txt"))
            .ReturnsAsync(new FileMetadata { Size = 42, ModifiedAt = mtime, IsDirectory = false });

        await _sut.WriteFileAsync("/docs/a.txt", Stream.Null, Ctx());

        var e = Single();
        Assert.Equal(FileChangeType.Created, e.ChangeType);
        Assert.Equal("docs/a.txt", e.Path);
        Assert.False(e.IsDirectory);
        Assert.Equal(42, e.Size);
        Assert.Equal(mtime, e.ModifiedAtUtc);
    }

    [Fact]
    public async Task WriteFileAsync_ExistingFile_AppendsModified()
    {
        _storage.Setup(s => s.IsDirectoryAsync("docs/a.txt")).ReturnsAsync(false);
        _storage.Setup(s => s.ExistsAsync("docs/a.txt")).ReturnsAsync(true);
        _storage.Setup(s => s.GetMetadataAsync("docs/a.txt"))
            .ReturnsAsync(new FileMetadata { Size = 7, ModifiedAt = DateTime.UtcNow });

        await _sut.WriteFileAsync("/docs/a.txt", Stream.Null, Ctx());

        Assert.Equal(FileChangeType.Modified, Single().ChangeType);
    }

    [Fact]
    public async Task CreateDirectoryAsync_AppendsCreatedDirectory()
    {
        await _sut.CreateDirectoryAsync("/docs/new", Ctx());

        var e = Single();
        Assert.Equal(FileChangeType.Created, e.ChangeType);
        Assert.Equal("docs/new", e.Path);
        Assert.True(e.IsDirectory);
    }

    [Fact]
    public async Task DeleteFileAsync_HardDelete_AppendsDeleted()
    {
        _storage.Setup(s => s.IsDirectoryAsync("docs/a.txt")).ReturnsAsync(false);

        await _sut.DeleteFileAsync("/docs/a.txt", Ctx(), isRecycleEnabled: false);

        var e = Single();
        Assert.Equal(FileChangeType.Deleted, e.ChangeType);
        Assert.Equal("docs/a.txt", e.Path);
    }

    [Fact]
    public async Task DeleteFileAsync_ToRecycleBin_AppendsRenameLeavingTheSubtree()
    {
        _storage.Setup(s => s.IsDirectoryAsync("docs/a.txt")).ReturnsAsync(false);
        // Recycle move returns the actual landing path.
        _storage.Setup(s => s.MoveAsync("docs/a.txt", It.IsAny<string>()))
            .ReturnsAsync((string _, string to) => to);

        await _sut.DeleteFileAsync("/docs/a.txt", Ctx(), isRecycleEnabled: true);

        var e = Single();
        Assert.Equal(FileChangeType.Renamed, e.ChangeType);
        Assert.Equal("docs/a.txt", e.OldPath);
        Assert.StartsWith(ShareEntryPolicyRecyclePrefix, e.Path);
    }

    // The recycle bin folder name is an internal policy detail; assert only that the destination
    // moved under it, not its exact spelling.
    private static readonly string ShareEntryPolicyRecyclePrefix =
        Core.Helpers.ShareEntryPolicy.RecycleBinName;

    [Fact]
    public async Task RenameAsync_File_AppendsRenamedWithOldPath()
    {
        _storage.Setup(s => s.IsDirectoryAsync("docs/a.txt")).ReturnsAsync(false);

        await _sut.RenameAsync("/docs/a.txt", "/docs/b.txt", Ctx());

        var e = Single();
        Assert.Equal(FileChangeType.Renamed, e.ChangeType);
        Assert.Equal("docs/a.txt", e.OldPath);
        Assert.Equal("docs/b.txt", e.Path);
    }

    [Fact]
    public async Task NoChangeLogConfigured_MutationsStillSucceed()
    {
        var noLog = new FileService(
            _storage.Object, _acl.Object, searchService: null, _shareId);
        _storage.Setup(s => s.IsDirectoryAsync("docs/a.txt")).ReturnsAsync(false);

        // Must not throw when no change log is wired.
        await noLog.DeleteFileAsync("/docs/a.txt", Ctx(), isRecycleEnabled: false);
        Assert.Empty(_log.Entries);
    }
}
