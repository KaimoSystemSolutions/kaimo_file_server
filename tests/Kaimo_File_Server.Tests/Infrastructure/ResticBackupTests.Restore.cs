using Xunit;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Infrastructure.Backup;
using Kaimo_File_Server.Infrastructure.Backup.Restic;
using Kaimo_File_Server.Infrastructure.ExternalStorage;
using Microsoft.Extensions.Configuration;

namespace Kaimo_File_Server.Tests;

public partial class ResticBackupTests
{
    // ─────────────── Restore: placement ───────────────

    [Fact]
    public void SourceRoot_IsThePathThatIsNotTheManifest()
    {
        var snapshot = new BackupSnapshot
        {
            PathsJson = """["/data/storage/pool01/projects","/data/kaimo-backups/restic-meta/share-1/kaimo-acl-manifest.json"]""",
        };
        Assert.Equal("/data/storage/pool01/projects", BackupRestoreExecutor.SourceRoot(snapshot));
        Assert.EndsWith("/kaimo-acl-manifest.json", BackupRestoreExecutor.ManifestPath(snapshot));
        Assert.Equal("/data/storage/pool01/projects/docs/a", BackupRestoreExecutor.SnapshotPath("/data/storage/pool01/projects/", "docs/a"));
        Assert.Equal("/data/storage/pool01/projects", BackupRestoreExecutor.SnapshotPath("/data/storage/pool01/projects", ""));
    }

    [Fact]
    public void MapPath_MapsTheSelectionOnly_AndLeavesUntouchedPathsAlone()
    {
        var items = new HashSet<string> { "a", "b.txt" };
        Assert.Equal("Restored/a/x.txt", BackupRestoreExecutor.MapPath("docs/a/x.txt", "docs", items, "Restored", []));
        Assert.Equal("docs/b.txt", BackupRestoreExecutor.MapPath("docs/b.txt", "docs", items, "docs", []));
        Assert.Null(BackupRestoreExecutor.MapPath("docs/c.txt", "docs", items, "docs", []));
        Assert.Null(BackupRestoreExecutor.MapPath("docs", "docs", items, "docs", []));
        Assert.Null(BackupRestoreExecutor.MapPath("docsX/a", "docs", items, "docs", []));
        Assert.Null(BackupRestoreExecutor.MapPath("docs/a/x.txt", "docs", items, "docs", ["docs/a"]));

        // Whole share root: everything but the root row itself.
        var all = new HashSet<string>();
        Assert.Equal("x/y", BackupRestoreExecutor.MapPath("x/y", "", all, "", []));
        Assert.Null(BackupRestoreExecutor.MapPath("", "", all, "", []));
    }

    [Fact]
    public void RestoredFolderName_IsNumberedWhenTaken()
    {
        var root = Directory.CreateTempSubdirectory("kaimo-restore-name-").FullName;
        try
        {
            var now = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
            var first = BackupRestoreExecutor.UniqueRestoredFolderName(root, "", now, System.Globalization.CultureInfo.InvariantCulture);
            Directory.CreateDirectory(Path.Combine(root, first));
            var second = BackupRestoreExecutor.UniqueRestoredFolderName(root, "", now, System.Globalization.CultureInfo.InvariantCulture);

            Assert.Matches(@"\d{4}-\d{2}-\d{2} \d{4}$", first);
            Assert.Equal(first + " (2)", second);
            // Named in the requesting admin's language, whatever culture the runner thread has.
            Assert.StartsWith("Wiederhergestellt ", BackupRestoreExecutor.UniqueRestoredFolderName(root, "", now,
                System.Globalization.CultureInfo.GetCultureInfo("de-DE")));
            Assert.StartsWith("Restored ", BackupRestoreExecutor.UniqueRestoredFolderName(root, "", now,
                System.Globalization.CultureInfo.GetCultureInfo("en-US")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Records which file contents were kept as a version before being overwritten.</summary>
    private sealed class CountingVersions
    {
        private readonly Moq.Mock<Kaimo_File_Server.Core.Services.File.IFileVersionService> _mock = new();

        public CountingVersions()
            => _mock.Setup(v => v.CaptureVersionAsync(Moq.It.IsAny<Guid>(), Moq.It.IsAny<string>(), Moq.It.IsAny<Stream>(), Moq.It.IsAny<string?>()))
                .Returns<Guid, string, Stream, string?>(async (_, path, content, _) =>
                {
                    if (SkipWith is { } skipped)
                        return new Kaimo_File_Server.Core.Services.File.VersionCaptureResult(skipped);
                    using var reader = new StreamReader(content, leaveOpen: true);
                    var text = await reader.ReadToEndAsync();
                    Created.Add((path, text));
                    return new Kaimo_File_Server.Core.Services.File.VersionCaptureResult(
                        Kaimo_File_Server.Core.Services.File.VersionCaptureOutcome.Created,
                        new FileVersion(Guid.NewGuid(), path, DateTime.UtcNow, "hash", "blob", text.Length, null, 1));
                });

        public List<(string Path, string Content)> Created { get; } = [];

        /// <summary>When set, every capture is refused with this outcome (e.g. a full pool).</summary>
        public Kaimo_File_Server.Core.Services.File.VersionCaptureOutcome? SkipWith { get; set; }

        public Kaimo_File_Server.Core.Services.File.IFileVersionService Service => _mock.Object;
    }

    /// <summary>Staging with docs/{a.txt,new.txt,sub/b.txt}; share with docs/{a.txt,extra.txt,sub/b.txt}.</summary>
    private static (string Root, string Staged, string Share) MergeFixture()
    {
        var root = Directory.CreateTempSubdirectory("kaimo-restore-merge-").FullName;
        var staged = Path.Combine(root, "stage");
        var share = Path.Combine(root, "share");
        Directory.CreateDirectory(Path.Combine(staged, "docs", "sub"));
        File.WriteAllText(Path.Combine(staged, "docs", "a.txt"), "backup-a");
        File.WriteAllText(Path.Combine(staged, "docs", "new.txt"), "backup-new");
        File.WriteAllText(Path.Combine(staged, "docs", "sub", "b.txt"), "backup-b");
        Directory.CreateDirectory(Path.Combine(share, "docs", "sub"));
        File.WriteAllText(Path.Combine(share, "docs", "a.txt"), "live-a");
        File.WriteAllText(Path.Combine(share, "docs", "extra.txt"), "live-extra");
        File.WriteAllText(Path.Combine(share, "docs", "sub", "b.txt"), "live-b");
        return (root, staged, share);
    }

    private static string Read(string share, string relative) => File.ReadAllText(Path.Combine(share, relative.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public async Task Merge_Overwrite_KeepsAVersion_RecreatesMissing_AndNeverDeletes()
    {
        var (root, staged, share) = MergeFixture();
        try
        {
            var versions = new CountingVersions();
            var outcome = new BackupRestoreOutcome();
            var merge = new RestoreMerge(share, Guid.NewGuid(), BackupRestoreConflict.Overwrite, versions.Service, "u1", " (restored 2026-10-10)", outcome);

            await merge.MoveAsync(Path.Combine(staged, "docs"), "docs");

            Assert.Equal("backup-a", Read(share, "docs/a.txt"));
            Assert.Equal("backup-new", Read(share, "docs/new.txt"));
            Assert.Equal("backup-b", Read(share, "docs/sub/b.txt"));
            Assert.Equal("live-extra", Read(share, "docs/extra.txt"));
            Assert.Equal([("docs/a.txt", "live-a"), ("docs/sub/b.txt", "live-b")], versions.Created.OrderBy(v => v.Path));
            Assert.Equal(2, outcome.Overwritten);
            Assert.Equal(2, outcome.VersionsCreated);
            Assert.Empty(merge.Untouched);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Merge_Overwrite_WhenCurrentContentCannotBeVersioned_RestoresBesideIt()
    {
        var (root, staged, share) = MergeFixture();
        try
        {
            var versions = new CountingVersions
            {
                SkipWith = Kaimo_File_Server.Core.Services.File.VersionCaptureOutcome.LowSpace,
            };
            var outcome = new BackupRestoreOutcome();
            var merge = new RestoreMerge(share, Guid.NewGuid(), BackupRestoreConflict.Overwrite, versions.Service, "u1", " (restored)", outcome);

            await merge.MoveAsync(Path.Combine(staged, "docs", "a.txt"), "docs/a.txt");

            Assert.Equal("live-a", Read(share, "docs/a.txt"));
            Assert.Equal("backup-a", Read(share, "docs/a (restored).txt"));
            Assert.Equal(0, outcome.Overwritten);
            Assert.Equal(1, outcome.Renamed);
            Assert.Equal(["docs/a.txt"], merge.Untouched);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(Kaimo_File_Server.Core.Services.File.VersionCaptureOutcome.NoPool)]
    [InlineData(Kaimo_File_Server.Core.Services.File.VersionCaptureOutcome.Disabled)]
    public async Task Merge_Overwrite_WhenShareIsNotVersionedAtAll_OverwritesAsChosen(
        Kaimo_File_Server.Core.Services.File.VersionCaptureOutcome outcomeOfCapture)
    {
        // Without versioning for the share there is no history to protect; the admin's
        // explicit "Overwrite" choice applies as it did before versioning policies existed.
        var (root, staged, share) = MergeFixture();
        try
        {
            var versions = new CountingVersions { SkipWith = outcomeOfCapture };
            var outcome = new BackupRestoreOutcome();
            var merge = new RestoreMerge(share, Guid.NewGuid(), BackupRestoreConflict.Overwrite, versions.Service, "u1", " (restored)", outcome);

            await merge.MoveAsync(Path.Combine(staged, "docs", "a.txt"), "docs/a.txt");

            Assert.Equal("backup-a", Read(share, "docs/a.txt"));
            Assert.False(File.Exists(Path.Combine(share, "docs", "a (restored).txt")));
            Assert.Equal(1, outcome.Overwritten);
            Assert.Equal(0, outcome.Renamed);
            Assert.Equal(0, outcome.VersionsCreated);
            Assert.Empty(merge.Untouched);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(BackupRestoreConflict.Overwrite)]
    [InlineData(BackupRestoreConflict.Rename)]
    public async Task Merge_IdenticalFile_IsLeftAlone_WithoutVersionOrCopy(BackupRestoreConflict conflict)
    {
        var (root, staged, share) = MergeFixture();
        try
        {
            File.WriteAllText(Path.Combine(staged, "docs", "same.txt"), "identical");
            File.WriteAllText(Path.Combine(share, "docs", "same.txt"), "identical");
            var versions = new CountingVersions();
            var outcome = new BackupRestoreOutcome();
            var merge = new RestoreMerge(share, Guid.NewGuid(), conflict, versions.Service, null, " (r)", outcome);

            await merge.MoveAsync(Path.Combine(staged, "docs", "same.txt"), "docs/same.txt");

            Assert.Equal(1, outcome.Unchanged);
            Assert.Empty(versions.Created);
            Assert.False(File.Exists(Path.Combine(share, "docs", "same (r).txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Merge_Skip_LeavesExistingFilesAndRecordsThem()
    {
        var (root, staged, share) = MergeFixture();
        try
        {
            var outcome = new BackupRestoreOutcome();
            var merge = new RestoreMerge(share, Guid.NewGuid(), BackupRestoreConflict.Skip, new CountingVersions().Service, null, " (r)", outcome);

            await merge.MoveAsync(Path.Combine(staged, "docs"), "docs");

            Assert.Equal("live-a", Read(share, "docs/a.txt"));
            Assert.Equal("backup-new", Read(share, "docs/new.txt"));
            Assert.Equal(2, outcome.Skipped);
            Assert.Equal(["docs/a.txt", "docs/sub/b.txt"], merge.Untouched.Order());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Merge_Rename_RestoresBesideTheExistingFile_Numbered()
    {
        var (root, staged, share) = MergeFixture();
        try
        {
            File.WriteAllText(Path.Combine(share, "docs", "a (restored 2026-10-10).txt"), "earlier restore");
            var outcome = new BackupRestoreOutcome();
            var merge = new RestoreMerge(share, Guid.NewGuid(), BackupRestoreConflict.Rename, new CountingVersions().Service, null, " (restored 2026-10-10)", outcome);

            await merge.MoveAsync(Path.Combine(staged, "docs"), "docs");

            Assert.Equal("live-a", Read(share, "docs/a.txt"));
            Assert.Equal("backup-a", Read(share, "docs/a (restored 2026-10-10) (2).txt"));
            Assert.Equal("backup-b", Read(share, "docs/sub/b (restored 2026-10-10).txt"));
            Assert.Equal(2, outcome.Renamed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Merge_FolderMeetingAFile_IsRestoredBesideIt_EvenWhenOverwriting()
    {
        var (root, staged, share) = MergeFixture();
        try
        {
            Directory.Delete(Path.Combine(share, "docs", "sub"), recursive: true);
            File.WriteAllText(Path.Combine(share, "docs", "sub"), "a file named sub");
            var outcome = new BackupRestoreOutcome();
            var merge = new RestoreMerge(share, Guid.NewGuid(), BackupRestoreConflict.Overwrite, new CountingVersions().Service, null, " (r)", outcome);

            await merge.MoveAsync(Path.Combine(staged, "docs", "sub"), "docs/sub");

            Assert.Equal("a file named sub", Read(share, "docs/sub"));
            Assert.Equal("backup-b", Read(share, "docs/sub (r)/b.txt"));
            Assert.Equal(1, outcome.Renamed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
