using System.Text;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Backup;
using Kaimo_File_Server.Infrastructure.Backup.Restic;
using Kaimo_File_Server.Tests.Infrastructure;
using Kaimo_File_Server.Web.Controllers;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Restore against the real EF schema (Sqlite): who may see and restore which backup points,
/// which mode needs which bit, and how backed-up permissions are written back.
/// </summary>
public sealed class ResticRestoreDatabaseTests : DatabaseTestBase
{
    private readonly User _user = new(Guid.NewGuid(), "Alice", "alice", "hash", "nt");
    private readonly Mock<IManagementAuthService> _auth = new();
    private readonly Mock<IBackupRunner> _runner = new();
    private BackupRestoreRequest? _queued;

    private UserContext Actor => new(_user, [], [], []);

    public ResticRestoreDatabaseTests()
    {
        _auth.Setup(a => a.HasGlobalPermissionAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>())).ReturnsAsync(false);
        Grant(ManagementPermission.ManageBackupJobs);
        _runner.Setup(r => r.EnqueueRestoreAsync(It.IsAny<BackupRestoreRequest>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback<BackupRestoreRequest, string, Guid, CancellationToken>((request, _, _, _) => _queued = request)
            .ReturnsAsync(Guid.NewGuid());
    }

    /// <summary>Grants each bit on the given shares only (empty = nowhere).</summary>
    private void Grant(ManagementPermission bit, params Guid[] shareIds)
        => _auth.Setup(a => a.GetAuthorizedShareIdsAsync(It.IsAny<UserContext>(), bit))
            .ReturnsAsync(AuthorizedScopeResult.LimitedTo(shareIds));

    /// <summary>Scoped restore operator: RestoreFromBackup on <paramref name="from"/>, in place on <paramref name="inPlace"/>.</summary>
    private void Restorer(Guid[] from, Guid[] inPlace)
    {
        Grant(ManagementPermission.RestoreFromBackup, from);
        Grant(ManagementPermission.RestoreBackupInPlace, inPlace);
        _auth.Setup(a => a.GetAuthorizedShareIdsAnyAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>()))
            .ReturnsAsync(AuthorizedScopeResult.LimitedTo(from.Union(inPlace).ToArray()));
    }

    private BackupCatalogService Catalog(bool demo = false)
    {
        var config = new ConfigurationBuilder().Build();
        return new BackupCatalogService(
            DbFactory,
            _auth.Object,
            new ResticTargetResolver(DbFactory, Mock.Of<ICredentialVault>(), config),
            new ResticClient(Mock.Of<IProcessRunner>(), config),
            _runner.Object,
            new DemoModeOptions { ReadOnly = demo },
            TimeProvider.System,
            NullLogger<BackupCatalogService>.Instance);
    }

    private sealed record Seed(ShareDefinition Projects, ShareDefinition Sales, ShareDefinition Homes, BackupRepository Repo);

    private async Task<Seed> SeedAsync()
    {
        var projects = new ShareDefinition("projects", Path.Combine(Path.GetTempPath(), "kaimo-restore-projects"));
        var sales = new ShareDefinition("sales", Path.Combine(Path.GetTempPath(), "kaimo-restore-sales"));
        var homes = new ShareDefinition("users", "/data/storage/pool01/users") { IsUserHomes = true };
        var repo = new BackupRepository { Name = "NAS", Backend = BackupBackend.Local, State = BackupRepositoryState.Active };
        await using var db = NewContext();
        db.Users.Add(_user);
        db.ShareDefinitions.AddRange(projects, sales, homes);
        db.BackupRepositories.Add(repo);
        foreach (var share in new[] { projects, sales, homes })
        {
            db.BackupSnapshots.Add(new BackupSnapshot
            {
                RepositoryId = repo.Id,
                SnapshotId = "snap-" + share.Name,
                Kind = BackupSnapshotKind.Share,
                ShareId = share.Id,
                TimeUtc = new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc),
                PathsJson = $"[\"/data/storage/pool01/{share.Name}\",\"/meta/share-x/kaimo-acl-manifest.json\"]",
            });
        }
        await db.SaveChangesAsync();
        return new Seed(projects, sales, homes, repo);
    }

    private static BackupRestoreRequest Request(Seed seed, BackupRestoreMode mode, bool permissions = false, params string[] items)
        => new(seed.Repo.Id, "snap-projects", seed.Projects.Id, "docs", items, mode, BackupRestoreConflict.Overwrite, permissions);

    // ── Visibility ──

    [Fact]
    public async Task ScopedRestorer_SeesOnlyItsShares_NeverTheHomesShare()
    {
        var seed = await SeedAsync();
        Restorer([seed.Projects.Id, seed.Homes.Id], []);

        var shares = await Catalog().ListRestorableSharesAsync(Actor);

        Assert.Equal(["projects"], shares.Select(s => s.Name));
        Assert.Single(await Catalog().ListBackupPointsAsync(Actor, seed.Projects.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Catalog().ListBackupPointsAsync(Actor, seed.Sales.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Catalog().ListBackupPointsAsync(Actor, seed.Homes.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Catalog().BrowseAsync(Actor, seed.Repo.Id, "snap-sales", ""));
    }

    [Fact]
    public async Task WithoutRestoreBits_TheRestoreTabStaysEmpty()
    {
        await SeedAsync();
        Restorer([], []);

        Assert.False((await Catalog().GetAccessAsync(Actor)).CanRestore);
        Assert.Empty(await Catalog().ListRestorableSharesAsync(Actor));
    }

    // ── Mode ↔ permission bit ──

    [Fact]
    public async Task NewFolder_NeedsRestoreFromBackup_AndIsQueuedNormalized()
    {
        var seed = await SeedAsync();
        Restorer([seed.Projects.Id], []);

        await Catalog().EnqueueRestoreAsync(Actor, Request(seed, BackupRestoreMode.NewFolder, items: ["a.txt", "a.txt"]) with
        {
            FolderPath = "./docs/",
            TargetShareId = seed.Sales.Id,
        });

        Assert.NotNull(_queued);
        Assert.Equal("docs", _queued.FolderPath);
        Assert.Equal(["a.txt"], _queued.Items);
        Assert.Null(_queued.TargetShareId);
        // The conflict policy only matters in place; a fresh folder never overwrites.
        Assert.Equal(BackupRestoreConflict.Skip, _queued.Conflict);
    }

    [Fact]
    public async Task InPlace_WithoutTheInPlaceBit_IsRejected()
    {
        var seed = await SeedAsync();
        Restorer([seed.Projects.Id], []);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Catalog().EnqueueRestoreAsync(Actor, Request(seed, BackupRestoreMode.InPlace)));
        Assert.Null(_queued);
    }

    [Fact]
    public async Task RestoringPermissions_NeedsTheInPlaceBitOnTheDestination()
    {
        var seed = await SeedAsync();
        Restorer([seed.Projects.Id], []);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Catalog().EnqueueRestoreAsync(Actor, Request(seed, BackupRestoreMode.NewFolder, permissions: true)));

        Restorer([seed.Projects.Id], [seed.Projects.Id]);
        await Catalog().EnqueueRestoreAsync(Actor, Request(seed, BackupRestoreMode.InPlace, permissions: true));
        Assert.True(_queued!.RestorePermissions);
    }

    [Fact]
    public async Task OtherShare_NeedsTheInPlaceBitOnTheTarget_AndAnExistingFolder()
    {
        var seed = await SeedAsync();
        Directory.CreateDirectory(Path.Combine(seed.Sales.Path, "inbox"));
        try
        {
            var request = Request(seed, BackupRestoreMode.OtherShare) with { TargetShareId = seed.Sales.Id, TargetFolder = "inbox" };
            Restorer([seed.Projects.Id], []);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Catalog().EnqueueRestoreAsync(Actor, request));

            Restorer([seed.Projects.Id], [seed.Sales.Id]);
            Assert.Equal("target_folder_missing", (await Assert.ThrowsAsync<ResticException>(() =>
                Catalog().EnqueueRestoreAsync(Actor, request with { TargetFolder = "missing" }))).Code);
            Assert.Equal("path_invalid", (await Assert.ThrowsAsync<ResticException>(() =>
                Catalog().EnqueueRestoreAsync(Actor, request with { TargetFolder = "../projects" }))).Code);

            await Catalog().EnqueueRestoreAsync(Actor, request);
            Assert.Equal(seed.Sales.Id, _queued!.TargetShareId);
            Assert.Equal("inbox", _queued.TargetFolder);
        }
        finally
        {
            Directory.Delete(seed.Sales.Path, recursive: true);
        }
    }

    [Theory]
    [InlineData("sub/a.txt")]
    [InlineData("..")]
    [InlineData(".kaimo-versions")]
    public async Task Items_MustBeEntriesOfTheFolder(string item)
    {
        var seed = await SeedAsync();
        Restorer([seed.Projects.Id], []);

        Assert.Equal("path_invalid", (await Assert.ThrowsAsync<ResticException>(() =>
            Catalog().EnqueueRestoreAsync(Actor, Request(seed, BackupRestoreMode.NewFolder, items: [item])))).Code);
    }

    [Fact]
    public async Task Restore_And_Download_AreBlockedInTheDemo()
    {
        var seed = await SeedAsync();
        Restorer([seed.Projects.Id], [seed.Projects.Id]);

        await Assert.ThrowsAsync<ReadOnlyDemoException>(() =>
            Catalog(demo: true).EnqueueRestoreAsync(Actor, Request(seed, BackupRestoreMode.NewFolder)));
        await Assert.ThrowsAsync<ReadOnlyDemoException>(() =>
            Catalog(demo: true).OpenDownloadAsync(Actor, seed.Repo.Id, "snap-projects", "docs/a.txt"));
    }

    [Fact]
    public async Task Download_NeedsRestoreFromBackup()
    {
        var seed = await SeedAsync();
        Restorer([], [seed.Projects.Id]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Catalog().OpenDownloadAsync(Actor, seed.Repo.Id, "snap-projects", "docs/a.txt"));
    }

    [Fact]
    public async Task DownloadEndpoint_RejectsKitTokens_AndForbidsOutOfScopeShares()
    {
        var seed = await SeedAsync();
        Restorer([seed.Projects.Id], []);
        var tokens = new BackupDownloadTokenService(DataProtectionProvider.Create("KaimoRestoreTests"));
        var users = new Mock<IUserRepository>();
        users.Setup(u => u.GetByIdAsync(_user.Id)).ReturnsAsync(_user);
        var contexts = new Mock<IUserContextFactory>();
        contexts.Setup(c => c.CreateAsync(It.IsAny<User>())).ReturnsAsync(Actor);
        ResticDownloadController Controller() => new(tokens, Catalog(), users.Object, contexts.Object, _auth.Object,
            new DemoModeOptions(), NullLogger<ResticDownloadController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var kitToken = tokens.Protect(ResticDownloadController.KitSubjectPrefix + seed.Repo.Id, _user.Id);
        Assert.IsType<UnauthorizedResult>(await Controller().Download(kitToken, CancellationToken.None));

        var foreign = tokens.Protect(ResticDownloadController.DownloadSubject(seed.Repo.Id, "snap-sales", "a.txt"), _user.Id);
        var result = Assert.IsType<StatusCodeResult>(await Controller().Download(foreign, CancellationToken.None));
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }

    // ── Activity ──

    [Fact]
    public async Task ScopedRestorer_SeesOwnRestoreRunsOnly()
    {
        var seed = await SeedAsync();
        Restorer([seed.Projects.Id], []);
        await using (var db = NewContext())
        {
            db.BackupRuns.AddRange(
                new BackupRun { RepositoryId = seed.Repo.Id, Type = BackupRunType.Restore, ActorUserId = _user.Id },
                new BackupRun { RepositoryId = seed.Repo.Id, Type = BackupRunType.Restore, ActorUserId = Guid.NewGuid() },
                new BackupRun { RepositoryId = seed.Repo.Id, Type = BackupRunType.Init });
            await db.SaveChangesAsync();
        }

        var runs = await Catalog().ListRunsAsync(Actor);

        Assert.Equal(_user.Id, Assert.Single(runs).ActorUserId);
    }

    // ── Permissions written back ──

    [Fact]
    public async Task AclRestore_WritesOwnersAndEntries_SkipsUnknownPrincipals_AndOnlyRestoredPaths()
    {
        var shareRoot = Directory.CreateTempSubdirectory("kaimo-acl-restore-").FullName;
        try
        {
            var share = new ShareDefinition("projects", shareRoot);
            var group = new Group(Guid.NewGuid(), "Engineers");
            var goneUser = Guid.NewGuid();
            var goneGroup = Guid.NewGuid();
            var existing = new FileMetadata
            {
                Id = Guid.NewGuid(), ShareId = share.Id, Path = "Restored/a.txt", Name = "a.txt", OwnerId = _user.Id,
                Acl = [new AccessEntry(_user.Id, AclEntryType.Deny, FilePermission.ListReadData, AclInheritance.ThisFolder)],
            };
            await using (var db = NewContext())
            {
                db.Users.Add(_user);
                db.Groups.Add(group);
                db.ShareDefinitions.Add(share);
                db.FileMetadata.Add(existing);
                await db.SaveChangesAsync();
            }
            Directory.CreateDirectory(Path.Combine(shareRoot, "Restored", "sub"));
            File.WriteAllText(Path.Combine(shareRoot, "Restored", "a.txt"), "x");

            var manifest = $$"""
                {"formatVersion":1,"exportedAtUtc":"2026-10-09T00:00:00Z","share":{"id":"{{share.Id}}"},"entries":[
                  {"path":"docs","isDirectory":true,"ownerId":"{{_user.Id}}","ownerName":"alice","acl":[]},
                  {"path":"docs/a.txt","isDirectory":false,"ownerId":"{{goneUser}}","ownerName":"bob","acl":[
                    {"principalId":"{{group.Id}}","principalName":"Engineers","entryType":0,"permissions":1,"inheritance":0},
                    {"principalId":"{{goneGroup}}","principalName":"Interns","entryType":0,"permissions":1,"inheritance":0}]},
                  {"path":"docs/sub","isDirectory":true,"ownerId":"{{_user.Id}}","ownerName":"alice","acl":[
                    {"principalId":"{{group.Id}}","principalName":"Engineers","entryType":0,"permissions":3,"inheritance":3}]},
                  {"path":"docs/missing.txt","isDirectory":false,"ownerId":"{{_user.Id}}","ownerName":"alice","acl":[]},
                  {"path":"other/x.txt","isDirectory":false,"ownerId":"{{_user.Id}}","ownerName":"alice","acl":[]}]}
                """;
            var actor = Guid.NewGuid();
            (int Applied, List<BackupSkippedPrincipal> Skipped) result;
            await using (var db = NewContext())
            await using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(manifest)))
                result = await BackupRestoreExecutor.ApplyAclAsync(db, stream, share.Id, shareRoot,
                    p => BackupRestoreExecutor.MapPath(p, "docs", new HashSet<string>(), "Restored", []),
                    actor, DateTime.UtcNow, CancellationToken.None);

            Assert.Equal(2, result.Applied);
            Assert.Equal(["bob", "Interns"], result.Skipped.Select(s => s.Name).Order());

            await using var check = NewContext();
            var rows = await check.FileMetadata.Include(m => m.Acl).Where(m => m.ShareId == share.Id).ToDictionaryAsync(m => m.Path);
            Assert.Equal(["Restored/a.txt", "Restored/sub"], rows.Keys.Order());
            // Existing row: unknown owner keeps the current one, ACL replaced by the backed-up one.
            Assert.Equal(_user.Id, rows["Restored/a.txt"].OwnerId);
            var ace = Assert.Single(rows["Restored/a.txt"].Acl);
            Assert.Equal(group.Id, ace.PrincipalId);
            Assert.Equal(AclEntryType.Allow, ace.EntryType);
            // New row for the folder, owner taken from the manifest.
            Assert.True(rows["Restored/sub"].IsDirectory);
            Assert.Equal(_user.Id, rows["Restored/sub"].OwnerId);
            Assert.Equal((FilePermission)3, Assert.Single(rows["Restored/sub"].Acl).Permissions);
        }
        finally
        {
            Directory.Delete(shareRoot, recursive: true);
        }
    }
}
