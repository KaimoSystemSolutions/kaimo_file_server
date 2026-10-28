using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Backup.Restic;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// F19: backup points of the home-folder share follow the live home rule — the share needs
/// <see cref="ManagementPermission.AccessHomeContents"/> anywhere, every home below it the
/// right to manage its owner (scope + rank), exactly like <c>AclService.HomeAccess</c>.
/// </summary>
public sealed partial class ResticRestoreDatabaseTests
{
    private readonly Guid _reachable = Guid.NewGuid();
    private readonly Guid _boss = Guid.NewGuid();

    /// <summary>Global restore operator (seeded BackupOperator); homes reachable as given.</summary>
    private void GlobalRestorer(bool accessHomes, params Guid[] reachableOwners)
    {
        _auth.Setup(a => a.GetAuthorizedShareIdsAsync(It.IsAny<UserContext>(), ManagementPermission.RestoreFromBackup))
            .ReturnsAsync(AuthorizedScopeResult.Unrestricted());
        _auth.Setup(a => a.GetAuthorizedShareIdsAsync(It.IsAny<UserContext>(), ManagementPermission.RestoreBackupInPlace))
            .ReturnsAsync(AuthorizedScopeResult.Unrestricted());
        _auth.Setup(a => a.GetAuthorizedShareIdsAnyAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>()))
            .ReturnsAsync(AuthorizedScopeResult.Unrestricted());
        _auth.Setup(a => a.HasAnyPermissionAsync(It.IsAny<UserContext>(), ManagementPermission.AccessHomeContents))
            .ReturnsAsync(accessHomes);
        _auth.Setup(a => a.CanManageUserAsync(It.IsAny<UserContext>(), It.IsAny<Guid>(), ManagementPermission.AccessHomeContents))
            .ReturnsAsync((UserContext _, Guid owner, ManagementPermission _) => reachableOwners.Contains(owner));
    }

    /// <summary>The homes root of the backup point holds both homes and a stray file.</summary>
    private void HomesRootListing()
    {
        const string root = "/data/storage/pool01/users";
        _lsOutput =
        [
            $$"""{"message_type":"node","name":"users","path":"{{root}}","type":"dir"}""",
            $$"""{"message_type":"node","name":"{{_reachable}}","path":"{{root}}/{{_reachable}}","type":"dir"}""",
            $$"""{"message_type":"node","name":"{{_boss}}","path":"{{root}}/{{_boss}}","type":"dir"}""",
            $$"""{"message_type":"node","name":"readme.txt","path":"{{root}}/readme.txt","type":"file"}""",
        ];
    }

    /// <summary>In place: a new folder is not possible at the homes root (see below).</summary>
    private static BackupRestoreRequest HomesRequest(Seed seed, string folder, params string[] items)
        => new(seed.Repo.Id, "snap-users", seed.Homes.Id, folder, items, BackupRestoreMode.InPlace, BackupRestoreConflict.Skip, true);

    [Fact]
    public async Task GlobalRestorer_WithoutAccessHomeContents_NeverReachesTheHomes()
    {
        var seed = await SeedAsync();
        GlobalRestorer(accessHomes: false);
        HomesRootListing();

        Assert.DoesNotContain("users", (await Catalog().ListRestorableSharesAsync(Actor)).Select(s => s.Name));
        Assert.DoesNotContain("users", (await Catalog().ListRestoreTargetSharesAsync(Actor)).Select(s => s.Name));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Catalog().ListBackupPointsAsync(Actor, seed.Homes.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Catalog().BrowseAsync(Actor, seed.Repo.Id, "snap-users", ""));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Catalog().OpenDownloadAsync(Actor, seed.Repo.Id, "snap-users", $"{_reachable}"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Catalog().EnqueueRestoreAsync(Actor, HomesRequest(seed, $"{_reachable}")));
        Assert.Null(_queued);
    }

    [Fact]
    public async Task GlobalRestorer_WithAccessHomeContents_ReachesOnlyOutrankedOwners()
    {
        var seed = await SeedAsync();
        GlobalRestorer(accessHomes: true, _reachable);
        HomesRootListing();

        Assert.Contains("users", (await Catalog().ListRestorableSharesAsync(Actor)).Select(s => s.Name));
        Assert.Single(await Catalog().ListBackupPointsAsync(Actor, seed.Homes.Id));
        var root = await Catalog().BrowseAsync(Actor, seed.Repo.Id, "snap-users", "");
        Assert.Equal([_reachable.ToString()], root.Select(e => e.Name));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Catalog().BrowseAsync(Actor, seed.Repo.Id, "snap-users", $"{_boss}/docs"));

        Assert.NotNull(await Catalog().OpenDownloadAsync(Actor, seed.Repo.Id, "snap-users", $"{_reachable}"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Catalog().OpenDownloadAsync(Actor, seed.Repo.Id, "snap-users", $"{_boss}"));

        await Catalog().EnqueueRestoreAsync(Actor, HomesRequest(seed, "", $"{_reachable}"));
        Assert.NotNull(_queued);
        _queued = null;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Catalog().EnqueueRestoreAsync(Actor, HomesRequest(seed, $"{_boss}/docs")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Catalog().EnqueueRestoreAsync(Actor, HomesRequest(seed, "", $"{_reachable}", $"{_boss}")));
        Assert.Null(_queued);
    }

    [Fact]
    public async Task WholeHomesRoot_IsRefused_WhenOneOwnerIsUnreachable()
    {
        var seed = await SeedAsync();
        HomesRootListing();

        GlobalRestorer(accessHomes: true, _reachable);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Catalog().EnqueueRestoreAsync(Actor, HomesRequest(seed, "")));
        Assert.Null(_queued);

        GlobalRestorer(accessHomes: true, _reachable, _boss);
        await Catalog().EnqueueRestoreAsync(Actor, HomesRequest(seed, ""));
        // The whole root means every home in it; the stray readme.txt is no home and is left out.
        Assert.Equal(new[] { _reachable.ToString(), _boss.ToString() }.Order(), _queued!.Items.Order());
    }

    [Fact]
    public async Task WholeHomesRoot_WithoutAnyHome_IsRefused()
    {
        var seed = await SeedAsync();
        GlobalRestorer(accessHomes: true, _reachable);
        _lsOutput =
        [
            """{"message_type":"node","name":"users","path":"/data/storage/pool01/users","type":"dir"}""",
            """{"message_type":"node","name":"readme.txt","path":"/data/storage/pool01/users/readme.txt","type":"file"}""",
        ];

        var error = await Assert.ThrowsAsync<ResticException>(() => Catalog().EnqueueRestoreAsync(Actor, HomesRequest(seed, "")));

        Assert.Equal("path_invalid", error.Code);
        Assert.Null(_queued);
    }

    [Fact]
    public async Task NewFolder_AtTheHomesRoot_IsRefused_InsideAHomeAllowed()
    {
        var seed = await SeedAsync();
        GlobalRestorer(accessHomes: true, _reachable);
        HomesRootListing();

        // "Restored ..." at the homes root is no home: the live home grant never exposes it.
        var error = await Assert.ThrowsAsync<ResticException>(() => Catalog().EnqueueRestoreAsync(Actor,
            HomesRequest(seed, "", $"{_reachable}") with { Mode = BackupRestoreMode.NewFolder }));
        Assert.Equal("homes_root_new_folder", error.Code);
        Assert.Null(_queued);

        await Catalog().EnqueueRestoreAsync(Actor, HomesRequest(seed, $"{_reachable}") with { Mode = BackupRestoreMode.NewFolder });
        Assert.Equal(BackupRestoreMode.NewFolder, _queued!.Mode);
    }

    [Fact]
    public async Task RestorableShares_FlagTheHomesShare()
    {
        await SeedAsync();
        GlobalRestorer(accessHomes: true, _reachable);

        var shares = await Catalog().ListRestorableSharesAsync(Actor);

        Assert.True(shares.Single(s => s.Name == "users").IsUserHomes);
        Assert.All(shares.Where(s => s.Name != "users"), s => Assert.False(s.IsUserHomes));
    }

    [Fact]
    public async Task OtherShare_IntoHomes_NeedsTheTargetFoldersOwner()
    {
        var seed = await SeedAsync();
        Directory.CreateDirectory(Path.Combine(seed.Homes.Path, _reachable.ToString()));
        Directory.CreateDirectory(Path.Combine(seed.Homes.Path, _boss.ToString()));
        try
        {
            GlobalRestorer(accessHomes: true, _reachable);
            var request = Request(seed, BackupRestoreMode.OtherShare) with { TargetShareId = seed.Homes.Id };

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                Catalog().EnqueueRestoreAsync(Actor, request with { TargetFolder = _boss.ToString() }));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                Catalog().EnqueueRestoreAsync(Actor, request with { TargetFolder = "" }));
            Assert.Null(_queued);

            await Catalog().EnqueueRestoreAsync(Actor, request with { TargetFolder = _reachable.ToString() });
            Assert.Equal(seed.Homes.Id, _queued!.TargetShareId);
        }
        finally
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
