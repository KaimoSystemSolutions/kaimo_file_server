using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Infrastructure.Backup;
using Kaimo_File_Server.Infrastructure.Backup.Restic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Infrastructure.Tests.Backup;

/// <summary>
/// A whole restore run into a share that hosts a virtual-share mount, with a stand-in for the
/// restic process: the executor must hand the live mount paths to the merge.
/// </summary>
public sealed partial class ResticRestoreDatabaseTests
{
    private const string MountedFolder = "Team/Cloud";

    /// <summary>Stages Team/new.txt and Team/Cloud/remote.txt on restore and serves the manifest on dump.</summary>
    private sealed class StagingRunner(string manifest) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string>? environment = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ProcessResult> RunStreamingAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string>? environment, Action<string>? onStdoutLine, bool clearEnvironment,
            CancellationToken cancellationToken = default)
        {
            if (arguments.Contains("restore"))
            {
                var target = arguments[arguments.ToList().IndexOf("--target") + 1];
                Directory.CreateDirectory(Path.Combine(target, "Team", "Cloud"));
                File.WriteAllText(Path.Combine(target, "Team", "new.txt"), "backup");
                File.WriteAllText(Path.Combine(target, "Team", "Cloud", "remote.txt"), "backup");
            }
            return Task.FromResult(new ProcessResult(0, "", ""));
        }

        public async Task<ProcessResult> RunToStreamAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string>? environment, Stream stdoutTarget, bool clearEnvironment,
            CancellationToken cancellationToken = default)
        {
            await stdoutTarget.WriteAsync(System.Text.Encoding.UTF8.GetBytes(manifest), cancellationToken);
            return new ProcessResult(0, "", "");
        }
    }

    private sealed record MountRestoreFixture(
        BackupRestoreExecutor Executor, ShareLockManager Locks, Mock<ICloudAccessMountGuard> Guard,
        ShareDefinition Share, Guid RunId, string ShareRoot);

    private async Task<MountRestoreFixture> MountRestoreFixtureAsync()
    {
        var shareRoot = Path.Combine(_root, "share");
        var repoPath = Path.Combine(_root, "repo");
        Directory.CreateDirectory(Path.Combine(shareRoot, "Team"));
        Directory.CreateDirectory(repoPath);

        var share = new ShareDefinition("projects", shareRoot);
        var repo = new BackupRepository
        {
            Name = "NAS",
            Backend = BackupBackend.Local,
            State = BackupRepositoryState.Active,
            SettingsJson = new ResticRepositorySettings { Path = repoPath }.Serialize(),
            EncryptedPassword = "protected",
        };
        var request = new BackupRestoreRequest(repo.Id, "snap", share.Id, "", ["Team"],
            BackupRestoreMode.InPlace, BackupRestoreConflict.Overwrite, RestorePermissions: true);
        var run = new BackupRun
        {
            RepositoryId = repo.Id,
            Type = BackupRunType.Restore,
            ActorUserId = _user.Id,
            DetailsJson = new BackupRestoreDetails(request).Serialize(),
        };
        await using (var db = NewContext())
        {
            db.Users.Add(_user);
            db.ShareDefinitions.Add(share);
            db.BackupRepositories.Add(repo);
            db.BackupSnapshots.Add(new BackupSnapshot
            {
                RepositoryId = repo.Id,
                SnapshotId = "snap",
                Kind = BackupSnapshotKind.Share,
                ShareId = share.Id,
                TimeUtc = new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc),
                PathsJson = "[\"/data/share\",\"/meta/share-x/kaimo-acl-manifest.json\"]",
            });
            db.BackupRuns.Add(run);
            await db.SaveChangesAsync();
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backup:Restic:LocalRoots"] = _root,
            ["Backup:Restic:CacheDirectory"] = Path.Combine(_root, "cache"),
            ["Backup:Restic:TempDirectory"] = Path.Combine(_root, "tmp"),
            ["Backup:Restic:RestoreDirectory"] = Path.Combine(_root, "restic-restore"),
        }).Build();
        var vault = new Mock<ICredentialVault>();
        vault.Setup(v => v.Unprotect<string>(It.IsAny<string>(), It.IsAny<CredentialContext>())).Returns("pw");
        var manifest = $$"""
            {"formatVersion":1,"entries":[
              {"path":"Team/new.txt","isDirectory":false,"ownerId":"{{_user.Id}}","ownerName":"alice","acl":[]},
              {"path":"{{MountedFolder}}","isDirectory":true,"ownerId":"{{_user.Id}}","ownerName":"alice","acl":[]}]}
            """;
        var locks = new ShareLockManager();
        var guard = new Mock<ICloudAccessMountGuard>();
        var executor = new BackupRestoreExecutor(DbFactory, new ResticTargetResolver(DbFactory, vault.Object, config),
            new ResticClient(new StagingRunner(manifest), config), Mock.Of<IFileVersionService>(), Mock.Of<IFileChangeLog>(),
            locks, config, TimeProvider.System, NullLogger<BackupRestoreExecutor>.Instance, guard.Object);
        return new MountRestoreFixture(executor, locks, guard, share, run.Id, shareRoot);
    }

    private async Task<(BackupRun Run, BackupRestoreOutcome? Result)> ExecuteAsync(MountRestoreFixture fixture)
    {
        await fixture.Executor.ExecuteAsync(fixture.RunId, (_, _) => { }, CancellationToken.None);
        await using var db = NewContext();
        var run = await db.BackupRuns.AsNoTracking().SingleAsync(r => r.Id == fixture.RunId);
        return (run, BackupRestoreDetails.TryParse(run.DetailsJson)?.Result);
    }

    [Fact]
    public async Task RestoreRun_LeavesANestedMountAlone_AndReportsItAsSkipped()
    {
        var fixture = await MountRestoreFixtureAsync();
        try
        {
            IReadOnlyList<string> mounts = [MountedFolder];
            fixture.Guard.Setup(g => g.GetMountPathsAsync(fixture.Share.Id, It.IsAny<CancellationToken>())).ReturnsAsync(mounts);

            var (run, result) = await ExecuteAsync(fixture);

            Assert.Equal(BackupRunStatus.SucceededWithWarnings, run.Status);
            Assert.False(Path.Exists(Path.Combine(fixture.ShareRoot, "Team", "Cloud")));
            Assert.Equal("backup", await File.ReadAllTextAsync(Path.Combine(fixture.ShareRoot, "Team", "new.txt")));
            Assert.Equal([MountedFolder], result!.SkippedItems);
            // The mount point gets no metadata row, so its host ACL stays as it is.
            await using var db = NewContext();
            Assert.Equal(["Team/new.txt"], await db.FileMetadata.AsNoTracking().Select(m => m.Path).ToListAsync());
        }
        finally
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreRun_ReadsTheMountsWhileHoldingTheShareLock()
    {
        var fixture = await MountRestoreFixtureAsync();
        try
        {
            int? freeLockSlots = null;
            IReadOnlyList<string> mounts = [];
            fixture.Guard.Setup(g => g.GetMountPathsAsync(fixture.Share.Id, It.IsAny<CancellationToken>()))
                .Callback(() => freeLockSlots = fixture.Locks.GetLock(fixture.Share.Name).CurrentCount)
                .ReturnsAsync(mounts);

            await ExecuteAsync(fixture);

            // A mount created while the run waited for the lock must still count.
            Assert.Equal(0, freeLockSlots);
        }
        finally
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreRun_WhenTheMountsCannotBeRead_FailsWithoutMovingAnything_AndReleasesTheLock()
    {
        var fixture = await MountRestoreFixtureAsync();
        try
        {
            fixture.Guard.Setup(g => g.GetMountPathsAsync(fixture.Share.Id, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("database unavailable"));

            var (run, _) = await ExecuteAsync(fixture);

            Assert.Equal(BackupRunStatus.Failed, run.Status);
            Assert.Equal("unexpected_error", run.ErrorCode);
            Assert.False(Path.Exists(Path.Combine(fixture.ShareRoot, "Team", "new.txt")));
            Assert.Equal(1, fixture.Locks.GetLock(fixture.Share.Name).CurrentCount);
        }
        finally
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
