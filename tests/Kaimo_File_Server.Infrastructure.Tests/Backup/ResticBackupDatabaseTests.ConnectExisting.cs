using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Security.Credentials;
using Kaimo_File_Server.Core.Security.Management;
using Kaimo_File_Server.Infrastructure.Backup;
using Kaimo_File_Server.Infrastructure.Backup.Restic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Infrastructure.Tests.Backup;

/// <summary>
/// Connecting a repository that already exists (for example from a previous installation):
/// its backup points must be usable right away, including share backup points whose share
/// does not exist on this server.
/// </summary>
public partial class ResticBackupDatabaseTests
{
    [Fact]
    public async Task ConnectingAnExistingRepository_LoadsItsBackupPointsRightAway()
    {
        var root = Path.Combine(Path.GetTempPath(), "kaimo-connect-" + Guid.NewGuid().ToString("N"));
        var repoPath = Path.Combine(root, "repo");
        Directory.CreateDirectory(repoPath);
        try
        {
            var oldShare = Guid.NewGuid().ToString("N");
            var runner = new ScriptedRunner(command => command switch
            {
                "cat" => (0, ["{\"version\":2,\"id\":\"repo-abc\",\"chunker_polynomial\":\"1\"}"]),
                "snapshots" => (0, ["[" +
                    "{\"id\":\"share1\",\"time\":\"2026-10-01T20:00:00Z\",\"paths\":[\"/data/storage/pool01/old\"]," +
                    "\"tags\":[\"kaimo\",\"share:" + oldShare + "\"]}," +
                    "{\"id\":\"sys1\",\"time\":\"2026-10-01T20:05:00Z\",\"paths\":[\"/data/kaimo-backups/restic-meta/system\"]," +
                    "\"tags\":[\"kaimo\",\"kind:system\",\"level:db\"]}]"]),
                _ => (0, []),
            });
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backup:Restic:LocalRoots"] = root,
                ["Backup:Restic:CacheDirectory"] = Path.Combine(root, "cache"),
                ["Backup:Restic:TempDirectory"] = Path.Combine(root, "tmp"),
            }).Build();
            var vault = new Mock<ICredentialVault>();
            vault.Setup(v => v.Protect(It.IsAny<string>(), It.IsAny<CredentialContext>())).Returns("protected");
            _auth.Setup(a => a.HasGlobalPermissionAsync(It.IsAny<Core.Domain.Identity.UserContext>(), ManagementPermission.ManageBackupRepositories))
                .ReturnsAsync(true);
            var draft = new BackupRepositoryDraft("Old NAS", BackupBackend.Local,
                new ResticRepositorySettings { Path = repoPath }, new ResticBackendSecrets(), false, 5, []);

            var repo = await Catalog(vault: vault.Object, config: config, runner: runner).CreateRepositoryAsync(Actor, draft, "old-password");

            await using var db = NewContext();
            var points = await db.BackupSnapshots.AsNoTracking().Where(s => s.RepositoryId == repo.Id)
                .OrderBy(s => s.SnapshotId).ToListAsync();
            Assert.Equal([("share1", BackupSnapshotKind.Share), ("sys1", BackupSnapshotKind.System)],
                points.Select(p => (p.SnapshotId, p.Kind)));
            Assert.Equal("repo-abc", repo.ResticRepositoryId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConnectingAnExistingRepository_StillSucceedsWhenItsBackupPointsCannotBeRead()
    {
        var root = Path.Combine(Path.GetTempPath(), "kaimo-connect-" + Guid.NewGuid().ToString("N"));
        var repoPath = Path.Combine(root, "repo");
        Directory.CreateDirectory(repoPath);
        try
        {
            // e.g. another installation holds an exclusive lock: the snapshot cache is filled by the next refresh.
            var runner = new ScriptedRunner(command => command switch
            {
                "cat" => (0, ["{\"version\":2,\"id\":\"repo-abc\",\"chunker_polynomial\":\"1\"}"]),
                "snapshots" => (11, []),
                _ => (0, []),
            });
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backup:Restic:LocalRoots"] = root,
                ["Backup:Restic:CacheDirectory"] = Path.Combine(root, "cache"),
                ["Backup:Restic:TempDirectory"] = Path.Combine(root, "tmp"),
            }).Build();
            var vault = new Mock<ICredentialVault>();
            vault.Setup(v => v.Protect(It.IsAny<string>(), It.IsAny<CredentialContext>())).Returns("protected");
            _auth.Setup(a => a.HasGlobalPermissionAsync(It.IsAny<Core.Domain.Identity.UserContext>(), ManagementPermission.ManageBackupRepositories))
                .ReturnsAsync(true);
            var draft = new BackupRepositoryDraft("Old NAS", BackupBackend.Local,
                new ResticRepositorySettings { Path = repoPath }, new ResticBackendSecrets(), false, 5, []);

            var repo = await Catalog(vault: vault.Object, config: config, runner: runner).CreateRepositoryAsync(Actor, draft, "old-password");

            await using var db = NewContext();
            Assert.Equal("repo-abc", (await db.BackupRepositories.AsNoTracking().SingleAsync(r => r.Id == repo.Id)).ResticRepositoryId);
            Assert.False(await db.BackupSnapshots.AnyAsync(s => s.RepositoryId == repo.Id));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConnectingAnExclusivelyLockedRepository_StillConnectsWithItsBackupPoints()
    {
        var root = Path.Combine(Path.GetTempPath(), "kaimo-connect-" + Guid.NewGuid().ToString("N"));
        var repoPath = Path.Combine(root, "repo");
        Directory.CreateDirectory(repoPath);
        try
        {
            // Another installation holds an exclusive lock (e.g. prune): every call that wants a lock fails.
            ScriptedRunner runner = null!;
            runner = new ScriptedRunner(command => !runner.Calls[^1].Contains("--no-lock") ? (11, []) : command switch
            {
                "cat" => (0, ["{\"version\":2,\"id\":\"repo-abc\",\"chunker_polynomial\":\"1\"}"]),
                "snapshots" => (0, ["[{\"id\":\"share1\",\"time\":\"2026-10-01T20:00:00Z\",\"paths\":[\"/data/storage/pool01/old\"]," +
                    "\"tags\":[\"kaimo\",\"share:" + Guid.NewGuid().ToString("N") + "\"]}]"]),
                _ => (0, []),
            });
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backup:Restic:LocalRoots"] = root,
                ["Backup:Restic:CacheDirectory"] = Path.Combine(root, "cache"),
                ["Backup:Restic:TempDirectory"] = Path.Combine(root, "tmp"),
            }).Build();
            var vault = new Mock<ICredentialVault>();
            vault.Setup(v => v.Protect(It.IsAny<string>(), It.IsAny<CredentialContext>())).Returns("protected");
            _auth.Setup(a => a.HasGlobalPermissionAsync(It.IsAny<Core.Domain.Identity.UserContext>(), ManagementPermission.ManageBackupRepositories))
                .ReturnsAsync(true);
            var draft = new BackupRepositoryDraft("Old NAS", BackupBackend.Local,
                new ResticRepositorySettings { Path = repoPath }, new ResticBackendSecrets(), false, 5, []);

            var repo = await Catalog(vault: vault.Object, config: config, runner: runner).CreateRepositoryAsync(Actor, draft, "old-password");

            await using var db = NewContext();
            Assert.Equal("repo-abc", repo.ResticRepositoryId);
            Assert.Equal(["share1"], await db.BackupSnapshots.Where(s => s.RepositoryId == repo.Id).Select(s => s.SnapshotId).ToListAsync());
            // Read-only listing: no lock taken, so nothing waits for one either.
            Assert.DoesNotContain("--retry-lock", runner.Calls.Single(c => c.Contains("snapshots")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Maintenance_TestRestore_SkipsSharePointsWhoseShareIsGone()
    {
        var runner = new ScriptedRunner(command => command switch
        {
            "forget" => (0, ["[]"]),
            "snapshots" => (0, ["[{\"id\":\"old1\",\"time\":\"2026-10-11T20:00:00Z\",\"paths\":[\"/data/storage/pool01/old\"]," +
                                "\"tags\":[\"kaimo\",\"share:" + Guid.NewGuid().ToString("N") + "\"]}]"]),
            _ => (0, []),
        });
        var fixture = await MaintenanceFixtureAsync(runner, appendOnly: false);
        await using (var db = NewContext())
        {
            (await db.BackupRepositories.SingleAsync(r => r.Id == fixture.Repo.Id)).TestRestoreSampleSize = 5;
            await db.SaveChangesAsync();
        }

        await fixture.Executor.ExecuteAsync(fixture.Repo.Id, BackupRunTrigger.Manual, null, (_, _) => { }, CancellationToken.None);

        // Its files cannot be compared with any live file: listing them would only waste the sample.
        Assert.DoesNotContain(runner.Calls, c => c.Contains("ls"));
        await using var check = NewContext();
        Assert.Equal(BackupRunStatus.Succeeded, (await check.BackupRuns.SingleAsync(r => r.Type == BackupRunType.TestRestore)).Status);
    }

    [Fact]
    public async Task ServerRestore_OffersSharePointsWhoseShareIsGone()
    {
        var (_, _, childShare, _, repo) = await SeedAsync(releaseToParent: true);
        await using (var db = NewContext())
        {
            db.BackupSnapshots.AddRange(
                new BackupSnapshot
                {
                    RepositoryId = repo.Id, SnapshotId = "orphan", Kind = BackupSnapshotKind.Share, ShareId = Guid.NewGuid(),
                    TimeUtc = DateTime.UtcNow,
                    PathsJson = "[\"/data/storage/pool01/old\",\"/data/kaimo-backups/restic-meta/share-x/kaimo-acl-manifest.json\"]",
                },
                new BackupSnapshot
                {
                    RepositoryId = repo.Id, SnapshotId = "live", Kind = BackupSnapshotKind.Share, ShareId = childShare.Id,
                    TimeUtc = DateTime.UtcNow, PathsJson = "[\"/data/storage/pool01/backend\"]",
                });
            await db.SaveChangesAsync();
        }
        _auth.Setup(a => a.HasGlobalPermissionAsync(It.IsAny<Core.Domain.Identity.UserContext>(), ManagementPermission.ManageBackupRepositories))
            .ReturnsAsync(true);

        var point = Assert.Single(await Catalog().ListServerBackupPointsAsync(Actor));
        Assert.Equal(("orphan", BackupSnapshotKind.Share), (point.SnapshotId, point.Kind));
        Assert.Contains("/data/storage/pool01/old", point.Source);
        Assert.DoesNotContain("kaimo-acl-manifest.json", point.Source);

        await Catalog().EnqueueServerRestoreAsync(Actor, repo.Id, "orphan");
        var ex = await Assert.ThrowsAsync<ResticException>(() => Catalog().EnqueueServerRestoreAsync(Actor, repo.Id, "live"));
        Assert.Equal("request_invalid", ex.Code);
    }
}
