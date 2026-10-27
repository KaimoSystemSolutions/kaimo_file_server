using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Storage;
using Kaimo_File_Server.Search;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

public partial class FileServiceTests
{
    // ═══════════════════ OpenAsync — per-intent ACL enforcement ═══════════════════

    private void SetupExistingFile()
    {
        _storageMock.Setup(s => s.ExistsAsync(It.IsAny<string>())).ReturnsAsync(true);
        _storageMock.Setup(s => s.IsDirectoryAsync(It.IsAny<string>())).ReturnsAsync(false);
        _storageMock
            .Setup(s => s.OpenAsync(
                It.IsAny<string>(), It.IsAny<OpenMode>(), It.IsAny<AccessIntent>(),
                It.IsAny<ShareIntent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IStorageHandle>(h => h.IsDirectory == false));
    }

    [Fact]
    public async Task OpenAsync_ReadWrite_WithWriteButNoRead_IsDenied()
    {
        // Regression: a user with write permission but WITHOUT read permission must not be
        // able to open a file ReadWrite (which would grant readable content). Write intent
        // used to skip the read check entirely.
        var ctx = CreateContext();
        SetupExistingFile();
        AllowAccess(FilePermission.CreateWriteData);
        DenyAccess(FilePermission.ListReadData);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.OpenAsync("/secret.txt", OpenMode.Open, AccessIntent.ReadWrite, ShareIntent.Read, ctx));
    }

    [Fact]
    public async Task OpenAsync_ReadWrite_WithBothPermissions_Succeeds()
    {
        var ctx = CreateContext();
        SetupExistingFile();
        AllowAccess(FilePermission.CreateWriteData);
        AllowAccess(FilePermission.ListReadData);

        var result = await _sut.OpenAsync(
            "/doc.txt", OpenMode.Open, AccessIntent.ReadWrite, ShareIntent.Read, ctx);

        Assert.NotNull(result.Session);
    }

    [Fact]
    public async Task OpenAsync_WriteOnly_WithWriteButNoRead_Succeeds()
    {
        // A pure write ("drop box") open must NOT require read permission.
        var ctx = CreateContext();
        SetupExistingFile();
        AllowAccess(FilePermission.CreateWriteData);
        DenyAccess(FilePermission.ListReadData);

        var result = await _sut.OpenAsync(
            "/upload.txt", OpenMode.Open, AccessIntent.Write, ShareIntent.Write, ctx);

        Assert.NotNull(result.Session);
    }

    [Fact]
    public async Task OpenAsync_ReadOnly_WithoutReadPermission_IsDenied()
    {
        var ctx = CreateContext();
        SetupExistingFile();
        DenyAccess(FilePermission.ListReadData);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.OpenAsync("/doc.txt", OpenMode.Open, AccessIntent.Read, ShareIntent.Read, ctx));
    }

    [Fact]
    public async Task RestoreFileVersionAsync_CorruptVersion_DoesNotWriteLiveFile()
    {
        var ctx = CreateContext();
        var versionService = new Mock<IFileVersionService>();
        var service = new FileService(
            _storageMock.Object, _aclMock.Object, null, _shareId,
            versionService.Object);
        var timestamp = DateTime.UtcNow;

        AllowAccess(FilePermission.CreateWriteData);
        _storageMock.Setup(s => s.ExistsAsync("document.txt")).ReturnsAsync(true);
        _storageMock.Setup(s => s.ReadAsync("document.txt", true))
            .ReturnsAsync(new MemoryStream([1, 2, 3]));
        versionService
            .Setup(v => v.CaptureVersionAsync(
                _shareId, "document.txt", It.IsAny<Stream>(), It.IsAny<string?>()))
            .ReturnsAsync(new VersionCaptureResult(VersionCaptureOutcome.Unchanged));
        versionService
            .Setup(v => v.ReadVersionAsync(_shareId, "document.txt", timestamp))
            .ThrowsAsync(new InvalidDataException("corrupt version blob"));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.RestoreFileVersionAsync("document.txt", timestamp, ctx));

        _storageMock.Verify(
            s => s.WriteAsync(
                It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ═══════════════════ ACL gates on secondary read/write paths ═══════════════════

    [Fact]
    public async Task FilterReadablePathsAsync_ReturnsOnlyAllowedNormalizedPaths()
    {
        var ctx = CreateContext();
        _aclMock
            .Setup(a => a.HasAccessBatchAsync(
                ctx, _shareId, It.IsAny<IReadOnlyList<(string, bool)>>(), FilePermission.ListReadData))
            .ReturnsAsync((UserContext _, Guid _, IReadOnlyList<(string Path, bool IsDir)> entries, FilePermission _) =>
                entries.ToDictionary(e => e.Path, e => !e.Path.Contains("secret")));

        var readable = await _sut.FilterReadablePathsAsync(
            [("/docs/a.txt", false), ("docs\\secret.txt", false), ("/docs", true)], ctx);

        Assert.True(readable.SetEquals(["docs/a.txt", "docs"]));
        Assert.Contains("DOCS/A.TXT", readable); // case-insensitive like the share namespace
    }

    [Fact]
    public async Task FilterReadablePathsAsync_Empty_SkipsAclLookup()
    {
        Assert.Empty(await _sut.FilterReadablePathsAsync([], CreateContext()));
        _aclMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReadFileVersionAsync_WithoutReadAccess_Throws()
    {
        var versions = new Mock<IFileVersionService>();
        var sut = new FileService(_storageMock.Object, _aclMock.Object, null, _shareId, versions.Object);
        DenyAccess(FilePermission.ListReadData);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => sut.ReadFileVersionAsync("doc.txt", DateTime.UtcNow, CreateContext()));
        versions.Verify(v => v.ReadVersionAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>()), Times.Never);
    }

    [Fact]
    public async Task ReadFileVersionAsync_WithReadAccess_ReadsFromThisShare()
    {
        var ts = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var versions = new Mock<IFileVersionService>();
        versions.Setup(v => v.ReadVersionAsync(_shareId, "dir/doc.txt", ts))
            .ReturnsAsync(new MemoryStream([1, 2, 3]));
        var sut = new FileService(_storageMock.Object, _aclMock.Object, null, _shareId, versions.Object);
        AllowAccess(FilePermission.ListReadData);

        await using var stream = await sut.ReadFileVersionAsync("/dir/doc.txt", ts, CreateContext());

        Assert.Equal(3, stream.Length);
    }

    [Fact]
    public async Task ReadFileVersionAsync_WithoutVersioning_NotSupported()
    {
        await Assert.ThrowsAsync<NotSupportedException>(
            () => _sut.ReadFileVersionAsync("doc.txt", DateTime.UtcNow, CreateContext()));
    }

    [Fact]
    public async Task OpenSnapshotAsync_WithoutReadAccess_Throws()
    {
        var versions = new Mock<IFileVersionService>();
        var sut = new FileService(_storageMock.Object, _aclMock.Object, null, _shareId, versions.Object);
        DenyAccess(FilePermission.ListReadData);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => sut.OpenSnapshotAsync("doc.txt", DateTime.UtcNow, CreateContext()));
        versions.Verify(v => v.ReadVersionAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>()), Times.Never);
    }

    [Fact]
    public async Task OpenSnapshotAsync_WithReadAccess_ReturnsSessionOverVersion()
    {
        var ts = DateTime.UtcNow;
        var versions = new Mock<IFileVersionService>();
        versions.Setup(v => v.ReadVersionAsync(_shareId, "doc.txt", ts))
            .ReturnsAsync(new MemoryStream([1]));
        var sut = new FileService(_storageMock.Object, _aclMock.Object, null, _shareId, versions.Object);
        AllowAccess(FilePermission.ListReadData);

        await using var session = await sut.OpenSnapshotAsync("doc.txt", ts, CreateContext());

        Assert.NotNull(session);
        versions.Verify(v => v.ReadVersionAsync(_shareId, "doc.txt", ts), Times.Once);
    }

    [Fact]
    public async Task SetModifiedAtAsync_WithoutWriteAccess_Throws()
    {
        _storageMock.Setup(s => s.IsDirectoryAsync("doc.txt")).ReturnsAsync(false);
        DenyAccess(FilePermission.CreateWriteData);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.SetModifiedAtAsync("doc.txt", CreateContext(), DateTime.UtcNow));
        _storageMock.Verify(s => s.SetModifiedDateAsync(It.IsAny<string>(), It.IsAny<DateTime>()), Times.Never);
    }

    [Fact]
    public async Task SetModifiedAtAsync_WithWriteAccess_UpdatesExistingFile()
    {
        var when = new DateTime(2025, 5, 5, 0, 0, 0, DateTimeKind.Utc);
        _storageMock.Setup(s => s.IsDirectoryAsync("doc.txt")).ReturnsAsync(false);
        _storageMock.Setup(s => s.ExistsAsync("doc.txt")).ReturnsAsync(true);
        AllowAccess(FilePermission.CreateWriteData);

        await _sut.SetModifiedAtAsync("doc.txt", CreateContext(), when);

        _storageMock.Verify(s => s.SetModifiedDateAsync("doc.txt", when), Times.Once);
    }
}
