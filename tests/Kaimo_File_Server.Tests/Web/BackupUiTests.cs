using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Web.Components.ViewModels.Backup;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// The repository editor offers only the tested storage types in a fixed order, and a local
/// repository path is edited as a sub-folder below the fixed backup root.
/// </summary>
public class BackupUiTests
{
    [Fact]
    public void SelectableBackends_OffersTestedTypesInFixedOrder()
        => Assert.Equal(
            [BackupBackend.Local, BackupBackend.Sftp, BackupBackend.Smb, BackupBackend.Rest, BackupBackend.Rclone, BackupBackend.S3],
            BackupUi.SelectableBackends(BackupBackend.Local));

    [Theory]
    [InlineData(BackupBackend.B2)]
    [InlineData(BackupBackend.Azure)]
    [InlineData(BackupBackend.Gcs)]
    [InlineData(BackupBackend.Swift)]
    public void SelectableBackends_KeepsHiddenTypeOfAnExistingRepository(BackupBackend current)
    {
        var backends = BackupUi.SelectableBackends(current);
        Assert.Equal(current, backends[^1]);
        Assert.Equal(7, backends.Count);
    }

    [Theory]
    [InlineData("/data/kaimo-backups/restic/", "/data/kaimo-backups/restic/repo1", "repo1")]
    [InlineData("/data/kaimo-backups/restic/", "/data/kaimo-backups/restic/a/b", "a/b")]
    [InlineData("/data/kaimo-backups/restic/", "/data/kaimo-backups/restic/", "")]
    [InlineData("/data/kaimo-backups/restic/", "", "")]
    public void LocalSubPath_StripsTheRoot(string prefix, string path, string expected)
        => Assert.Equal(expected, BackupUi.LocalSubPath(path, prefix));

    [Theory]
    [InlineData("/data/kaimo-backups/restic/", "/mnt/elsewhere/repo")]
    [InlineData("/data/kaimo-backups/restic/", "/data/kaimo-backups/resticx/repo")]
    public void LocalSubPath_IsNullOutsideTheRoot(string prefix, string path)
        => Assert.Null(BackupUi.LocalSubPath(path, prefix));
}
