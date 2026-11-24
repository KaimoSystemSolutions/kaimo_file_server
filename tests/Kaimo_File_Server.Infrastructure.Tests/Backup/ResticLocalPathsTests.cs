using Kaimo_File_Server.Infrastructure.Backup.Restic;
using Xunit;

namespace Kaimo_File_Server.Infrastructure.Tests.Backup;

/// <summary>
/// Local repositories live below the backup folder mounted into the container. The UI and the
/// recovery kit show that folder as "/backups" (as the administrator knows it from the host),
/// and connecting an existing repository suggests the repository folders found below the roots.
/// </summary>
public class ResticLocalPathsTests
{
    private const string BackupRoot = "/data/kaimo-backups";

    [Theory]
    [InlineData("/data/kaimo-backups/restic/nas", "/backups/restic/nas")]
    [InlineData("/data/kaimo-backups/restic/", "/backups/restic/")]
    [InlineData("/data/kaimo-backups", "/backups")]
    public void ToDisplay_MapsTheBackupRootToBackups(string path, string expected)
        => Assert.Equal(expected, ResticLocalPaths.ToDisplay(path, BackupRoot));

    [Fact]
    public void ToDisplay_AcceptsABackupRootWithTrailingSlash()
        => Assert.Equal("/backups/restic/nas", ResticLocalPaths.ToDisplay("/data/kaimo-backups/restic/nas", BackupRoot + "/"));

    [Theory]
    [InlineData("/data/kaimo-backups-old/restic")]
    [InlineData("/mnt/usb/restic")]
    [InlineData("")]
    public void ToDisplay_LeavesOtherPathsUnchanged(string path)
        => Assert.Equal(path, ResticLocalPaths.ToDisplay(path, BackupRoot));

    [Theory]
    [InlineData("/data/kaimo-backups/restic/nas", true)]
    [InlineData("/data/kaimo-backups", true)]
    [InlineData("/data/kaimo-backups-old/restic", false)]
    [InlineData("/backups-offsite/nas", false)] // starts with "/backups", but is not the mounted folder
    [InlineData("/backups/nas", false)]
    [InlineData("", false)]
    public void IsInBackupRoot_OnlyForTheMountedBackupFolder(string path, bool expected)
        => Assert.Equal(expected, ResticLocalPaths.IsInBackupRoot(path, BackupRoot));

    [Fact]
    public void IsInBackupRoot_HoldsWhenTheBackupRootIsCalledBackupsItself()
        => Assert.True(ResticLocalPaths.IsInBackupRoot("/backups/restic/nas", "/backups"));

    [Theory]
    [InlineData("/backups/restic/nas", "/data/kaimo-backups/restic/nas")]
    [InlineData("/backups/restic/", "/data/kaimo-backups/restic/")]
    [InlineData("/backups", "/data/kaimo-backups")]
    [InlineData("/backups-offsite/nas", "/backups-offsite/nas")]
    [InlineData("/mnt/usb/restic", "/mnt/usb/restic")]
    [InlineData("", "")]
    public void FromDisplay_MapsBackupsBackToTheContainerPath(string display, string expected)
        => Assert.Equal(expected, ResticLocalPaths.FromDisplay(display, BackupRoot));

    [Theory]
    [InlineData("/data/kaimo-backups/restic/nas")]
    [InlineData("/data/kaimo-backups")]
    [InlineData("/mnt/usb/restic")]
    public void FromDisplay_ReversesToDisplay(string path)
        => Assert.Equal(path, ResticLocalPaths.FromDisplay(ResticLocalPaths.ToDisplay(path, BackupRoot), BackupRoot));

    [Fact]
    public void FindRepositories_SkippedFolders_DoNotCountAgainstTheLimit()
    {
        var root = Path.Combine(Path.GetTempPath(), "kaimo-find-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repos = Enumerable.Range(0, 5).Select(i => Repo(root, "repo" + i)).ToList();
            var connected = repos.Take(3).ToHashSet();

            var found = ResticLocalPaths.FindRepositories([root], maxResults: 2, skip: connected.Contains);

            Assert.Equal([repos[3], repos[4]], found);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindRepositories_FindsRepositoryFoldersBelowTheRoots_UpToTheDepthLimit()
    {
        var root = Path.Combine(Path.GetTempPath(), "kaimo-find-" + Guid.NewGuid().ToString("N"));
        try
        {
            var direct = Repo(root, "old-nas");
            var nested = Repo(root, "archive", "2025");
            Repo(root, "a", "b", "c", "too-deep");
            Directory.CreateDirectory(Path.Combine(root, "half", "keys")); // no config file
            File.WriteAllText(Path.Combine(root, "only-config"), "");
            Directory.CreateDirectory(Path.Combine(direct, "data", "inner", "keys")); // inside a repository: not descended
            File.WriteAllText(Path.Combine(direct, "data", "inner", "config"), "");

            var found = ResticLocalPaths.FindRepositories([root, Path.Combine(root, "missing")], maxDepth: 3);

            Assert.Equal([nested, direct], found);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindRepositories_IsBounded()
    {
        var root = Path.Combine(Path.GetTempPath(), "kaimo-find-" + Guid.NewGuid().ToString("N"));
        try
        {
            for (var i = 0; i < 5; i++)
                Repo(root, "repo" + i);

            Assert.Equal(3, ResticLocalPaths.FindRepositories([root], maxDepth: 3, maxResults: 3).Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Creates a folder that looks like a restic repository (config file + keys folder).</summary>
    private static string Repo(string root, params string[] segments)
    {
        var path = Path.Combine([root, .. segments]);
        Directory.CreateDirectory(Path.Combine(path, "keys"));
        File.WriteAllText(Path.Combine(path, "config"), "");
        return path;
    }
}
