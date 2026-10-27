using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services.File;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

public partial class FileServiceTests
{
    // ═══════════════════ Restore must not lose the current content ═══════════════════

    private (FileService Service, Mock<IFileVersionService> Versions, DateTime Timestamp) SetupRestore(
        VersionCaptureOutcome captureOutcome, bool fileExists = true)
    {
        var versions = new Mock<IFileVersionService>();
        var service = new FileService(
            _storageMock.Object, _aclMock.Object, null, _shareId, versions.Object);
        var timestamp = DateTime.UtcNow;

        AllowAccess(FilePermission.CreateWriteData);
        _storageMock.Setup(s => s.ExistsAsync("document.txt")).ReturnsAsync(fileExists);
        _storageMock.Setup(s => s.ReadAsync("document.txt", true))
            .ReturnsAsync(() => new MemoryStream([1, 2, 3]));
        versions
            .Setup(v => v.CaptureVersionAsync(
                _shareId, "document.txt", It.IsAny<Stream>(), It.IsAny<string?>()))
            .ReturnsAsync(new VersionCaptureResult(captureOutcome));
        versions
            .Setup(v => v.ReadVersionAsync(_shareId, "document.txt", timestamp))
            .ReturnsAsync(() => new MemoryStream([9, 9]));
        return (service, versions, timestamp);
    }

    private void VerifyLiveFileWritten(Times times)
        => _storageMock.Verify(
            s => s.WriteAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()),
            times);

    [Theory]
    [InlineData(VersionCaptureOutcome.LowSpace)]
    [InlineData(VersionCaptureOutcome.TooLarge)]
    [InlineData(VersionCaptureOutcome.Excluded)]
    [InlineData(VersionCaptureOutcome.Disabled)]
    [InlineData(VersionCaptureOutcome.NoPool)]
    public async Task RestoreFileVersionAsync_CurrentContentNotPreserved_RefusesAndKeepsLiveFile(
        VersionCaptureOutcome outcome)
    {
        var (service, versions, timestamp) = SetupRestore(outcome);

        var ex = await Assert.ThrowsAsync<VersionBackupUnavailableException>(() =>
            service.RestoreFileVersionAsync("document.txt", timestamp, CreateContext()));

        Assert.Equal(outcome, ex.Outcome);
        VerifyLiveFileWritten(Times.Never());
        versions.Verify(v => v.ReadVersionAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>()),
            Times.Never);
    }

    [Fact]
    public async Task RestoreFileVersionAsync_AllowWithoutBackup_RestoresAnyway()
    {
        var (service, _, timestamp) = SetupRestore(VersionCaptureOutcome.LowSpace);

        await service.RestoreFileVersionAsync("document.txt", timestamp, CreateContext(), allowWithoutBackup: true);

        VerifyLiveFileWritten(Times.Once());
    }

    [Theory]
    [InlineData(VersionCaptureOutcome.Created)]
    [InlineData(VersionCaptureOutcome.Unchanged)]
    public async Task RestoreFileVersionAsync_CurrentContentPreserved_Restores(VersionCaptureOutcome outcome)
    {
        var (service, _, timestamp) = SetupRestore(outcome);

        await service.RestoreFileVersionAsync("document.txt", timestamp, CreateContext());

        VerifyLiveFileWritten(Times.Once());
    }

    [Fact]
    public async Task RestoreFileVersionAsync_FileMissing_RestoresWithoutSnapshot()
    {
        var (service, versions, timestamp) = SetupRestore(VersionCaptureOutcome.LowSpace, fileExists: false);

        await service.RestoreFileVersionAsync("document.txt", timestamp, CreateContext());

        VerifyLiveFileWritten(Times.Once());
        versions.Verify(v => v.CaptureVersionAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string?>()), Times.Never);
    }
}
