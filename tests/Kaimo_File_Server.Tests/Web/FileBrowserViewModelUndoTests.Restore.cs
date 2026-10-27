using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Services.File;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed partial class FileBrowserViewModelUndoTests
{
    // ═══════════════════ Restore without a backup of the current content ═══════════════════

    [Fact]
    public async Task RestoreVersion_BackupUnavailable_AsksForConsent()
    {
        await _sut.LoadShareAsync("share", "docs");
        _fileService
            .Setup(f => f.RestoreFileVersionAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<UserContext>(), false))
            .ThrowsAsync(new VersionBackupUnavailableException(VersionCaptureOutcome.LowSpace));

        var result = await _sut.RestoreVersionAsync(Item("docs/b.txt"), DateTime.UtcNow);

        Assert.False(result.Success);
        Assert.True(result.RequiresBackupConsent);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task RestoreVersion_WithConsent_PassesItToTheFileService()
    {
        await _sut.LoadShareAsync("share", "docs");
        var ts = DateTime.UtcNow;

        var result = await _sut.RestoreVersionAsync(Item("docs/b.txt"), ts, allowWithoutBackup: true);

        Assert.True(result.Success);
        _fileService.Verify(f => f.RestoreFileVersionAsync(
            "docs/b.txt", ts, It.IsAny<UserContext>(), true), Times.Once);
    }

    [Fact]
    public async Task RestoreVersion_OtherFailure_DoesNotAskForConsent()
    {
        await _sut.LoadShareAsync("share", "docs");
        _fileService
            .Setup(f => f.RestoreFileVersionAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<UserContext>(), false))
            .ThrowsAsync(new IOException("disk"));

        var result = await _sut.RestoreVersionAsync(Item("docs/b.txt"), DateTime.UtcNow);

        Assert.False(result.Success);
        Assert.False(result.RequiresBackupConsent);
    }
}
