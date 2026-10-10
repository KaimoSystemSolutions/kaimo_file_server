using Xunit;
using System.Text.Json;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Backup;
using Kaimo_File_Server.Infrastructure.Backup.Restic;
using Kaimo_File_Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Backup catalog authorization and the ACL manifest against the real EF schema (Sqlite).
/// </summary>
public class ResticBackupDatabaseTests : DatabaseTestBase
{
    private readonly User _user = new(Guid.NewGuid(), "Alice", "alice", "hash", "nt");
    private readonly Mock<IManagementAuthService> _auth = new();

    private UserContext Actor => new(_user, [], [], []);

    public ResticBackupDatabaseTests()
    {
        // No restore rights unless a test grants them.
        _auth.Setup(a => a.GetAuthorizedShareIdsAnyAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>()))
            .ReturnsAsync(AuthorizedScopeResult.LimitedTo([]));
        _auth.Setup(a => a.GetAuthorizedShareIdsAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>()))
            .ReturnsAsync(AuthorizedScopeResult.LimitedTo([]));
    }

    private BackupCatalogService Catalog(bool demo = false)
    {
        var config = new ConfigurationBuilder().Build();
        return new BackupCatalogService(
            DbFactory,
            _auth.Object,
            new ResticTargetResolver(DbFactory, Mock.Of<ICredentialVault>(), config),
            new ResticClient(Mock.Of<IProcessRunner>(), config),
            Mock.Of<IBackupRunner>(),
            new DemoModeOptions { ReadOnly = demo },
            TimeProvider.System,
            NullLogger<BackupCatalogService>.Instance);
    }

    /// <summary>A department-scoped job admin for the given shares, no repository rights.</summary>
    private void ScopedJobAdmin(params Guid[] shareIds)
    {
        _auth.Setup(a => a.HasGlobalPermissionAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>())).ReturnsAsync(false);
        _auth.Setup(a => a.GetAuthorizedShareIdsAsync(It.IsAny<UserContext>(), ManagementPermission.ManageBackupJobs))
            .ReturnsAsync(AuthorizedScopeResult.LimitedTo(shareIds));
    }

    private async Task<(Department Parent, Department Child, ShareDefinition ChildShare, ShareDefinition OtherShare, BackupRepository Repo)> SeedAsync(
        bool releaseToParent)
    {
        await using var db = NewContext();
        var parent = new Department("Engineering");
        var child = new Department("Backend", parentDepartmentId: parent.Id);
        var other = new Department("Sales");
        db.Departments.AddRange(parent, child, other);
        var childShare = new ShareDefinition("backend", "/data/storage/pool01/backend", child.Id);
        var otherShare = new ShareDefinition("sales", "/data/storage/pool01/sales", other.Id);
        db.ShareDefinitions.AddRange(childShare, otherShare);
        var repo = new BackupRepository { Name = "NAS", Backend = BackupBackend.Local, State = BackupRepositoryState.Active };
        db.BackupRepositories.Add(repo);
        if (releaseToParent)
            db.BackupRepositoryDepartments.Add(new BackupRepositoryDepartment { RepositoryId = repo.Id, DepartmentId = parent.Id });
        await db.SaveChangesAsync();
        return (parent, child, childShare, otherShare, repo);
    }

    private static BackupJobDraft Draft(Guid repositoryId, params Guid[] shareIds)
        => new(null, "Nightly", repositoryId, true, false, shareIds, [new TimeOnly(2, 0)], [DayOfWeek.Monday]);

    [Fact]
    public async Task SaveJob_ScopedAdmin_OnRepositoryReleasedToParentDepartment_Succeeds()
    {
        var (_, _, childShare, _, repo) = await SeedAsync(releaseToParent: true);
        ScopedJobAdmin(childShare.Id);

        var job = await Catalog().SaveJobAsync(Actor, Draft(repo.Id, childShare.Id));

        await using var db = NewContext();
        var saved = await db.BackupJobs.Include(j => j.Sources).SingleAsync(j => j.Id == job.Id);
        Assert.Equal(childShare.Id, Assert.Single(saved.Sources).ShareId);
        Assert.Equal(_user.Id, saved.CreatedByUserId);
        Assert.Equal([new TimeOnly(2, 0)], saved.Schedule.Times);
        // A new job never catches up on slots from before it existed.
        Assert.NotNull(saved.LastScheduledSlotUtc);
    }

    [Theory]
    [InlineData(false, true)]   // weekdays left, all start times removed
    [InlineData(true, false)]   // start times left, all weekdays removed
    [InlineData(false, false)]
    public async Task SaveJob_WithoutTimesOrDays_IsStoredAsManualOnly(bool withTimes, bool withDays)
    {
        var (_, _, childShare, _, repo) = await SeedAsync(releaseToParent: true);
        ScopedJobAdmin(childShare.Id);

        var job = await Catalog().SaveJobAsync(Actor, Draft(repo.Id, childShare.Id) with
        {
            Times = withTimes ? [new TimeOnly(2, 0)] : [],
            Days = withDays ? Enum.GetValues<DayOfWeek>() : [],
        });

        await using var db = NewContext();
        var schedule = (await db.BackupJobs.SingleAsync(j => j.Id == job.Id)).Schedule;
        Assert.Empty(schedule.Times);
        Assert.Empty(schedule.Days);
        Assert.True(schedule.IsEmpty);
    }

    [Fact]
    public async Task SaveJob_ScopedAdmin_OnUnreleasedRepository_IsRejected()
    {
        var (_, _, childShare, _, repo) = await SeedAsync(releaseToParent: false);
        ScopedJobAdmin(childShare.Id);

        var ex = await Assert.ThrowsAsync<ResticException>(() => Catalog().SaveJobAsync(Actor, Draft(repo.Id, childShare.Id)));
        Assert.Equal("repository_not_usable", ex.Code);
    }

    [Fact]
    public async Task SaveJob_ScopedAdmin_WithShareOutsideScope_IsRejected()
    {
        var (_, _, childShare, otherShare, repo) = await SeedAsync(releaseToParent: true);
        ScopedJobAdmin(childShare.Id);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Catalog().SaveJobAsync(Actor, Draft(repo.Id, childShare.Id, otherShare.Id)));
        await using var db = NewContext();
        Assert.False(await db.BackupJobs.AnyAsync());
    }

    [Fact]
    public async Task SaveJob_ScopedAdmin_CannotBackUpTheHomesShare()
    {
        var (_, child, _, _, repo) = await SeedAsync(releaseToParent: true);
        var homes = new ShareDefinition("users", "/data/storage/pool01/users", child.Id) { IsUserHomes = true };
        await using (var db = NewContext())
        {
            db.ShareDefinitions.Add(homes);
            await db.SaveChangesAsync();
        }
        ScopedJobAdmin(homes.Id);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Catalog().SaveJobAsync(Actor, Draft(repo.Id, homes.Id)));
        Assert.DoesNotContain(await Catalog().ListSelectableSharesAsync(Actor), s => s.Id == homes.Id);
    }

    [Fact]
    public async Task ScopedAdmin_DoesNotSeeOtherDepartmentsJobs()
    {
        var (_, _, childShare, otherShare, repo) = await SeedAsync(releaseToParent: true);
        await using (var db = NewContext())
        {
            db.BackupJobs.Add(new BackupJob
            {
                Name = "Sales nightly",
                RepositoryId = repo.Id,
                Sources = [new BackupJobSource { ShareId = otherShare.Id }],
            });
            await db.SaveChangesAsync();
        }
        ScopedJobAdmin(childShare.Id);

        Assert.Empty(await Catalog().ListJobsAsync(Actor));
        Assert.Empty(await Catalog().ListRunsAsync(Actor));
    }

    [Fact]
    public async Task RepositoryOperations_RequireGlobalPermission_AndDemoBlocksWrites()
    {
        await SeedAsync(releaseToParent: true);
        ScopedJobAdmin();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Catalog().ListRepositoriesAsync(Actor));

        _auth.Setup(a => a.HasGlobalPermissionAsync(It.IsAny<UserContext>(), ManagementPermission.ManageBackupRepositories)).ReturnsAsync(true);
        Assert.Single(await Catalog().ListRepositoriesAsync(Actor));
        var draft = new BackupRepositoryDraft("x", BackupBackend.Local, new ResticRepositorySettings(), new ResticBackendSecrets(), false, 5, []);
        await Assert.ThrowsAsync<ReadOnlyDemoException>(() => Catalog(demo: true).CreateRepositoryAsync(Actor, draft, null));
    }

    [Fact]
    public async Task DeletingAShare_CascadesToItsJobSources()
    {
        var (_, _, childShare, _, repo) = await SeedAsync(releaseToParent: true);
        await using (var db = NewContext())
        {
            db.BackupJobs.Add(new BackupJob { Name = "j", RepositoryId = repo.Id, Sources = [new BackupJobSource { ShareId = childShare.Id }] });
            await db.SaveChangesAsync();
        }
        await using (var db = NewContext())
        {
            db.ShareDefinitions.Remove(await db.ShareDefinitions.SingleAsync(s => s.Id == childShare.Id));
            await db.SaveChangesAsync();
        }
        await using var check = NewContext();
        Assert.False(await check.BackupJobSources.AnyAsync());
        Assert.True(await check.BackupJobs.AnyAsync());
    }

    [Fact]
    public async Task AclManifest_ContainsOwnersAndEntriesWithPrincipalNames()
    {
        var share = new ShareDefinition("projects", "/data/storage/pool01/projects");
        var group = new Group(Guid.NewGuid(), "Engineers");
        await using (var db = NewContext())
        {
            db.Users.Add(_user);
            db.Groups.Add(group);
            db.ShareDefinitions.Add(share);
            db.FileMetadata.Add(new FileMetadata
            {
                Id = Guid.NewGuid(), ShareId = share.Id, Path = "docs", Name = "docs", IsDirectory = true, OwnerId = _user.Id,
                Acl = [new AccessEntry(group.Id, AclEntryType.Allow, FilePermission.TakeOwnership, AclInheritance.ThisFolder)],
            });
            await db.SaveChangesAsync();
        }

        var path = Path.Combine(Path.GetTempPath(), "kaimo-manifest-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await using (var db = NewContext())
                await BackupAclManifest.WriteAsync(db, share, path, DateTime.UtcNow, CancellationToken.None);

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var root = doc.RootElement;
            Assert.Equal(BackupAclManifest.FormatVersion, root.GetProperty("formatVersion").GetInt32());
            Assert.Equal(share.Id, root.GetProperty("share").GetProperty("id").GetGuid());
            var entry = Assert.Single(root.GetProperty("entries").EnumerateArray());
            Assert.Equal("docs", entry.GetProperty("path").GetString());
            Assert.Equal("alice", entry.GetProperty("ownerName").GetString());
            var ace = Assert.Single(entry.GetProperty("acl").EnumerateArray());
            Assert.Equal("Engineers", ace.GetProperty("principalName").GetString());
            Assert.Equal((long)FilePermission.TakeOwnership, ace.GetProperty("permissions").GetInt64());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
