using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Storage;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Core.Tests.Services.Files;

/// <summary>Sync pulls: same access rules and bookkeeping as an upload, but no version snapshot.</summary>
public partial class FileServiceTests
{
    private static readonly DateTime PulledAt = new(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Func<Stream, CancellationToken, Task> NoContent = (_, _) => Task.CompletedTask;

    private void StorageWrites(string path)
        => _storageMock.Setup(s => s.WriteAsync(
                path, It.IsAny<Func<Stream, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns((string _, Func<Stream, CancellationToken, Task> writeContent, CancellationToken token)
                => writeContent(Stream.Null, token));

    [Fact]
    public async Task ReplaceFile_RunsTheWriterThroughTheAtomicStorageWrite()
    {
        bool wrote = false;
        StorageWrites("a.bin");
        AllowAccess(FilePermission.CreateWriteData);

        await _sut.ReplaceFileAsync("a.bin", (_, _) =>
        {
            wrote = true;
            return Task.CompletedTask;
        }, CreateContext(), new FileTimes(null, PulledAt, PulledAt));

        Assert.True(wrote);
    }

    [Fact]
    public async Task ReplaceFile_StampsTheSourceTimes()
    {
        StorageWrites("a.bin");
        AllowAccess(FilePermission.CreateWriteData);

        await _sut.ReplaceFileAsync("a.bin", NoContent, CreateContext(), new FileTimes(null, PulledAt, PulledAt));

        _storageMock.Verify(s => s.SetModifiedDateAsync("a.bin", PulledAt), Times.Once);
        _storageMock.Verify(s => s.SetAccessedDateAsync("a.bin", PulledAt), Times.Once);
    }

    [Fact]
    public async Task ReplaceFile_TakesNoVersionSnapshot()
    {
        var versions = new Mock<IFileVersionService>();
        StorageWrites("a.bin");
        AllowAccess(FilePermission.CreateWriteData);
        var sut = new FileService(_storageMock.Object, _aclMock.Object, null, _shareId, versions.Object);

        await sut.ReplaceFileAsync("a.bin", NoContent, CreateContext(), new FileTimes(null, PulledAt, PulledAt));

        versions.Verify(v => v.CreateVersionAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task WriteFile_StillTakesAVersionSnapshot()
    {
        var versions = new Mock<IFileVersionService>();
        _storageMock.Setup(s => s.ReadAsync("a.bin", true)).ReturnsAsync(() => new MemoryStream([1]));
        AllowAccess(FilePermission.CreateWriteData);
        var sut = new FileService(_storageMock.Object, _aclMock.Object, null, _shareId, versions.Object);

        await sut.WriteFileAsync("a.bin", new MemoryStream([1]), CreateContext());

        versions.Verify(v => v.CreateVersionAsync(
            _shareId, "a.bin", It.IsAny<Stream>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task ReplaceFile_WriterFails_StampsNothing()
    {
        _storageMock.Setup(s => s.WriteAsync(
                "a.bin", It.IsAny<Func<Stream, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("connection lost"));
        AllowAccess(FilePermission.CreateWriteData);

        await Assert.ThrowsAsync<IOException>(() => _sut.ReplaceFileAsync(
            "a.bin", NoContent, CreateContext(), new FileTimes(null, PulledAt, PulledAt)));

        _storageMock.Verify(s => s.SetModifiedDateAsync(It.IsAny<string>(), It.IsAny<DateTime>()), Times.Never);
    }

    [Fact]
    public async Task ReplaceFile_WithoutWriteAccess_NeverReachesTheStorage()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _sut.ReplaceFileAsync(
            "a.bin", NoContent, CreateContext(), new FileTimes(null, PulledAt, PulledAt)));

        _storageMock.Verify(s => s.WriteAsync(
            It.IsAny<string>(), It.IsAny<Func<Stream, CancellationToken, Task>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private void ExistingFileLastReadAt(string path, DateTime accessed)
    {
        _storageMock.Setup(s => s.ExistsAsync(path)).ReturnsAsync(true);
        _storageMock.Setup(s => s.GetMetadataAsync(path)).ReturnsAsync(new FileMetadata
        {
            Path = path,
            ModifiedAt = PulledAt.AddDays(-2),
            CreatedAt = PulledAt.AddDays(-3),
            LastAccessedAt = accessed
        });
    }

    [Fact]
    public async Task ReplaceFile_Overwrite_AccessTimeFollowsTheSourceEvenIfTheOldFileWasReadLater()
    {
        StorageWrites("a.bin");
        AllowAccess(FilePermission.CreateWriteData);
        ExistingFileLastReadAt("a.bin", PulledAt.AddDays(1));

        await _sut.ReplaceFileAsync("a.bin", NoContent, CreateContext(), new FileTimes(null, PulledAt, PulledAt));

        _storageMock.Verify(s => s.SetAccessedDateAsync("a.bin", PulledAt), Times.Once);
    }

    [Fact]
    public async Task WriteFile_Overwrite_KeepsANewerAccessTime()
    {
        var readLater = PulledAt.AddDays(1);
        AllowAccess(FilePermission.CreateWriteData);
        ExistingFileLastReadAt("a.bin", readLater);

        await _sut.WriteFileAsync("a.bin", new MemoryStream([1]), CreateContext(), new FileTimes(null, PulledAt, PulledAt));

        _storageMock.Verify(s => s.SetAccessedDateAsync("a.bin", readLater), Times.Once);
    }

    [Fact]
    public async Task DeleteAbandonedUploads_AsksTheStorageForFilesOlderThanADay()
    {
        AllowAccess(FilePermission.CreateWriteData);

        await _sut.DeleteAbandonedUploadsAsync("docs", CreateContext());

        _storageMock.Verify(s => s.DeleteAbandonedUploadsAsync("docs", It.Is<DateTime>(cutoff =>
            cutoff < DateTime.UtcNow.AddHours(-23) && cutoff > DateTime.UtcNow.AddHours(-25))), Times.Once);
    }

    [Fact]
    public async Task DeleteAbandonedUploads_WithoutWriteAccess_NeverReachesTheStorage()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.DeleteAbandonedUploadsAsync("docs", CreateContext()));

        _storageMock.Verify(s => s.DeleteAbandonedUploadsAsync(It.IsAny<string>(), It.IsAny<DateTime>()), Times.Never);
    }

    [Fact]
    public async Task ReplaceFile_ReservedNamespace_IsRejected()
    {
        AllowAccess(FilePermission.CreateWriteData);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _sut.ReplaceFileAsync(
            ".kaimo-internal/a.bin", NoContent, CreateContext(), new FileTimes(null, PulledAt, PulledAt)));
    }
}
