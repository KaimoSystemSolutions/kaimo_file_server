using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Storage;
using Kaimo_File_Server.Tests.Infrastructure;
using Kaimo_File_Server.Web.Components.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Regression tests that pin <see cref="ShareListViewModel"/> mutations to the ACTUAL
/// database state. Each test runs the view-model method against real repositories over
/// an in-memory Sqlite schema, then re-reads the row through a fresh context to prove
/// the value truly changed (or, on the negative paths, did not).
///
/// These guard against the classic silent regressions a mock-only test misses:
///   • a toggle that flips the in-memory object but never saves,
///   • a rename that reports success without updating the row,
///   • a delete that returns true while the row survives,
///   • a validation gate that lets a bad value through to the store.
/// </summary>
public class ShareListViewModelDatabaseTests : DatabaseTestBase
{
    private readonly Mock<IManagementAuthService> _mgmtAuth = new();
    private readonly Mock<IAclService> _aclService = new();
    private readonly Mock<IFileVersionService> _versions = new();
    private readonly ShareLockManager _lockManager = new();
    private readonly string _storagePath;
    private readonly IReadOnlyList<string> _storagePools;

    public ShareListViewModelDatabaseTests()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(), "kaimo-tests", Guid.NewGuid().ToString("N"));
        _storagePath = Path.Combine(testRoot, "pool-01");
        _storagePools =
        [
            _storagePath,
            Path.Combine(testRoot, "pool-02")
        ];
    }

    private void CleanupPools()
    {
        var testRoot = Path.GetDirectoryName(_storagePath);
        if (testRoot is not null && Directory.Exists(testRoot))
            Directory.Delete(testRoot, recursive: true);
    }

    private ShareDefinition SeedPoolShare(
        string name,
        bool isEnabled = true,
        bool isHidden = false,
        bool isRecycleEnabled = false,
        Guid? departmentId = null)
        => SeedShare(
            name,
            Path.Combine(_storagePools[0], name),
            isEnabled,
            isHidden,
            isRecycleEnabled,
            departmentId);

    /// <summary>
    /// Builds the SUT for an actor who is an unrestricted (Global) share admin — the
    /// common case for exercising the management mutations.
    /// </summary>
    private ShareListViewModel BuildAdminSut(
        User actor,
        ICloudSyncOperationCoordinator? cloudSyncOperations = null)
    {
        var ctx = ContextFor(actor);

        _mgmtAuth
            .Setup(m => m.GetAuthorizedShareIdsAnyAsync(It.IsAny<UserContext>(), ManagementPermission.ShareAdmin))
            .ReturnsAsync(AuthorizedScopeResult.Unrestricted());
        _mgmtAuth
            .Setup(m => m.HasAnyPermissionAsync(It.IsAny<UserContext>(), ManagementPermission.CreateShares))
            .ReturnsAsync(true);
        // Global admin → may perform every per-share management action.
        _mgmtAuth
            .Setup(m => m.CanManageShareAsync(It.IsAny<UserContext>(), It.IsAny<Guid>(), It.IsAny<ManagementPermission>()))
            .ReturnsAsync(true);

        return new ShareListViewModel(
            ShareRepo(), UserRepo(), GroupRepo(), AclRepo(), FileMetadataRepo(),
            _aclService.Object, _mgmtAuth.Object,
            UserContextFactoryFor((actor.Username, ctx)).Object,
            _lockManager,
            AuthStateFor(actor.Username),
            NullLogger<ShareListViewModel>.Instance,
            _storagePools,
            _versions.Object,
            cloudSyncOperations: cloudSyncOperations);
    }

    private async Task<(ShareListViewModel Sut, ShareDefinition Share)> LoadAndSelectAsync(
        ShareDefinition seeded,
        User actor,
        ICloudSyncOperationCoordinator? cloudSyncOperations = null)
    {
        var sut = BuildAdminSut(actor, cloudSyncOperations);
        await sut.LoadAsync();
        var share = sut.Shares.Single(s => s.Id == seeded.Id);
        sut.SelectShare(share);
        return (sut, share);
    }

    // ─────────────────────── Toggle enabled ───────────────────────

    [Fact]
    public async Task ToggleShareEnabledAsync_PersistsFlippedFlag()
    {
        var actor = SeedUser("admin");
        var seeded = SeedPoolShare("docs", isEnabled: true);
        var (sut, _) = await LoadAndSelectAsync(seeded, actor);

        var ok = await sut.ToggleShareEnabledAsync();

        Assert.True(ok);
        await using var db = NewContext();
        var row = await db.ShareDefinitions.FindAsync(seeded.Id);
        Assert.False(row!.IsEnabled); // flipped true → false AND saved
    }

    [Fact]
    public async Task ToggleRecycleEnabledAsync_PersistsFlippedFlag()
    {
        var actor = SeedUser("admin");
        var seeded = SeedPoolShare("docs", isRecycleEnabled: false);
        var (sut, _) = await LoadAndSelectAsync(seeded, actor);

        var ok = await sut.ToggleRecycleEnabledAsync();

        Assert.True(ok);
        await using var db = NewContext();
        var row = await db.ShareDefinitions.FindAsync(seeded.Id);
        Assert.True(row!.IsRecycleEnabled);
    }

    [Fact]
    public async Task ToggleShareHiddenAsync_PersistsFlippedFlag()
    {
        var actor = SeedUser("admin");
        var seeded = SeedPoolShare("docs", isHidden: false);
        var (sut, _) = await LoadAndSelectAsync(seeded, actor);

        var ok = await sut.ToggleShareHiddenAsync();

        Assert.True(ok);
        await using var db = NewContext();
        var row = await db.ShareDefinitions.FindAsync(seeded.Id);
        Assert.True(row!.IsShareHidden);
    }

    // ─────────────── Per-permission enforcement (ManageShareAccess vs EditShareSettings) ───────────────

    /// <summary>
    /// SUT for an actor who may edit share SETTINGS but does NOT hold ManageShareAccess.
    /// The coarse listing gate still lists the share (so the razor would render controls),
    /// which is exactly why the per-action server guard must be the real enforcement.
    /// </summary>
    private ShareListViewModel BuildSettingsOnlyManagerSut(User actor)
    {
        var ctx = ContextFor(actor);

        _mgmtAuth
            .Setup(m => m.GetAuthorizedShareIdsAnyAsync(It.IsAny<UserContext>(), ManagementPermission.ShareAdmin))
            .ReturnsAsync(AuthorizedScopeResult.Unrestricted());
        _mgmtAuth
            .Setup(m => m.CanManageShareAsync(It.IsAny<UserContext>(), It.IsAny<Guid>(), ManagementPermission.EditShareSettings))
            .ReturnsAsync(true);
        _mgmtAuth
            .Setup(m => m.CanManageShareAsync(It.IsAny<UserContext>(), It.IsAny<Guid>(), ManagementPermission.ManageShareAccess))
            .ReturnsAsync(false);

        return new ShareListViewModel(
            ShareRepo(), UserRepo(), GroupRepo(), AclRepo(), FileMetadataRepo(),
            _aclService.Object, _mgmtAuth.Object,
            UserContextFactoryFor((actor.Username, ctx)).Object,
            _lockManager,
            AuthStateFor(actor.Username),
            NullLogger<ShareListViewModel>.Instance,
            _storagePools);
    }

    [Fact]
    public async Task ToggleShareHiddenAsync_WithoutManageShareAccess_IsDeniedAndNotPersisted()
    {
        var actor = SeedUser("editor");
        var seeded = SeedPoolShare("docs", isHidden: false);

        var sut = BuildSettingsOnlyManagerSut(actor);
        await sut.LoadAsync();
        sut.SelectShare(sut.Shares.Single(s => s.Id == seeded.Id));

        var ok = await sut.ToggleShareHiddenAsync();

        Assert.False(ok);
        await using var db = NewContext();
        var row = await db.ShareDefinitions.FindAsync(seeded.Id);
        Assert.False(row!.IsShareHidden); // guard blocked the mutation
    }

    [Fact]
    public async Task ToggleRecycleEnabledAsync_WithEditShareSettings_Succeeds()
    {
        var actor = SeedUser("editor");
        var seeded = SeedPoolShare("docs", isRecycleEnabled: false);

        var sut = BuildSettingsOnlyManagerSut(actor);
        await sut.LoadAsync();
        sut.SelectShare(sut.Shares.Single(s => s.Id == seeded.Id));

        var ok = await sut.ToggleRecycleEnabledAsync();

        Assert.True(ok); // EditShareSettings is sufficient here
        await using var db = NewContext();
        var row = await db.ShareDefinitions.FindAsync(seeded.Id);
        Assert.True(row!.IsRecycleEnabled);
    }

    [Fact]
    public async Task Toggle_WithNoSelection_DoesNothing()
    {
        var actor = SeedUser("admin");
        SeedPoolShare("docs", isEnabled: true);
        var sut = BuildAdminSut(actor);
        await sut.LoadAsync(); // loaded, but nothing selected

        var ok = await sut.ToggleShareEnabledAsync();

        Assert.False(ok);
        await using var db = NewContext();
        Assert.True((await db.ShareDefinitions.SingleAsync()).IsEnabled); // untouched
    }

    // ─────────────────────── Rename ───────────────────────

    [Fact]
    public async Task RenameShareAsync_PersistsNewNameAndPath()
    {
        var actor = SeedUser("admin");
        var seeded = SeedPoolShare("oldname");
        var (sut, _) = await LoadAndSelectAsync(seeded, actor);

        sut.EditShareName = "newname";
        var ok = await sut.RenameShareAsync();

        Assert.True(ok);
        await using var db = NewContext();
        var row = await db.ShareDefinitions.FindAsync(seeded.Id);
        Assert.Equal("newname", row!.Name);
        Assert.Equal("newname", Path.GetFileName(row.Path)); // only final component changed
    }

    [Fact]
    public async Task RenameShareAsync_KeepsCloudSyncAttachedToNewShareRoot()
    {
        var actor = SeedUser("admin");
        var seeded = SeedPoolShare("oldname");
        seeded.CloudSettings.Folders["team/docs"] = new SyncedFolder(
            "google", new Dictionary<string, string>());
        await ShareRepo().UpdateAsync(seeded);
        var (sut, _) = await LoadAndSelectAsync(seeded, actor);

        sut.EditShareName = "newname";
        var ok = await sut.RenameShareAsync();

        Assert.True(ok);
        await using var db = NewContext();
        var row = await db.ShareDefinitions.FindAsync(seeded.Id);
        Assert.NotNull(row);
        Assert.True(row.CloudSettings.Folders.ContainsKey("team/docs"));
        Assert.Equal(
            Path.Combine(_storagePath, "newname", "team", "docs"),
            Path.Combine(row.Path, "team", "docs"));
    }

    [Fact]
    public async Task RenameShareAsync_PreservesCloudPathChangedAfterUiLoaded()
    {
        var actor = SeedUser("admin");
        var seeded = SeedPoolShare("oldname");
        seeded.CloudSettings.Folders["team/old"] = new SyncedFolder(
            "google", new Dictionary<string, string>());
        await ShareRepo().UpdateAsync(seeded);
        var (sut, _) = await LoadAndSelectAsync(seeded, actor);

        // Simulate a folder rename after the management page loaded its copy.
        var latest = await ShareRepo().GetByIdAsync(seeded.Id);
        var folder = latest!.CloudSettings.Folders["team/old"];
        latest.CloudSettings.Folders.Remove("team/old");
        latest.CloudSettings.Folders["team/new"] = folder;
        await ShareRepo().UpdateAsync(latest);

        sut.EditShareName = "newname";
        Assert.True(await sut.RenameShareAsync());

        await using var db = NewContext();
        var row = await db.ShareDefinitions.FindAsync(seeded.Id);
        Assert.NotNull(row);
        Assert.False(row.CloudSettings.Folders.ContainsKey("team/old"));
        Assert.True(row.CloudSettings.Folders.ContainsKey("team/new"));
    }

    [Fact]
    public async Task RenameShareAsync_ActiveCloudSync_IsRejectedWithoutChangingDatabase()
    {
        var actor = SeedUser("admin");
        var seeded = SeedPoolShare("oldname");
        var operations = new InMemoryCloudSyncOperationCoordinator(TimeProvider.System);
        var syncLease = await operations.TryBeginSyncAsync(
            seeded.Id, "team/docs");
        Assert.NotNull(syncLease);
        await using var activeSync = syncLease;
        var (sut, _) = await LoadAndSelectAsync(seeded, actor, operations);

        sut.EditShareName = "newname";
        var ok = await sut.RenameShareAsync();

        Assert.False(ok);
        Assert.Equal(Resources.Web_Error_ShareInUse, sut.EditErrorMessage);
        await using var db = NewContext();
        var row = await db.ShareDefinitions.FindAsync(seeded.Id);
        Assert.Equal("oldname", row!.Name);
        Assert.Equal("oldname", Path.GetFileName(row.Path));
    }

    [Fact]
    public async Task RenameShareAsync_ToExistingName_IsRejectedAndDoesNotPersist()
    {
        var actor = SeedUser("admin");
        var seeded = SeedPoolShare("oldname");
        SeedPoolShare("taken");
        var (sut, _) = await LoadAndSelectAsync(seeded, actor);

        sut.EditShareName = "taken";
        var ok = await sut.RenameShareAsync();

        Assert.False(ok);
        Assert.NotNull(sut.EditErrorMessage);
        await using var db = NewContext();
        Assert.Equal("oldname", (await db.ShareDefinitions.FindAsync(seeded.Id))!.Name);
    }

    [Fact]
    public async Task RenameShareAsync_WithInvalidName_IsRejectedAndDoesNotPersist()
    {
        var actor = SeedUser("admin");
        var seeded = SeedPoolShare("oldname");
        var (sut, _) = await LoadAndSelectAsync(seeded, actor);

        sut.EditShareName = "in valid/name"; // spaces + slash are disallowed
        var ok = await sut.RenameShareAsync();

        Assert.False(ok);
        await using var db = NewContext();
        Assert.Equal("oldname", (await db.ShareDefinitions.FindAsync(seeded.Id))!.Name);
    }

    // ─────────────────────── Delete ───────────────────────

    [Fact]
    public async Task DeleteShareAsync_RemovesRowFromStore()
    {
        var actor = SeedUser("admin");
        var seeded = SeedPoolShare("docs");
        var (sut, _) = await LoadAndSelectAsync(seeded, actor);

        var ok = await sut.DeleteShareAsync();

        Assert.True(ok);
        _aclService.Verify(a => a.DeleteShareMetadataAsync(seeded.Id), Times.Once);
        _versions.Verify(v => v.DeleteShareAsync(seeded.Id), Times.Once);
        await using var db = NewContext();
        Assert.False(await db.ShareDefinitions.AnyAsync(s => s.Id == seeded.Id));
    }

    // ─────────────────────── Create validation ───────────────────────

    [Theory]
    [InlineData("")]              // empty
    [InlineData("bad name")]      // space
    [InlineData("bad/name")]      // slash
    [InlineData(".hidden")]       // leading dot
    public async Task CreateShareAsync_WithInvalidName_IsRejectedAndPersistsNothing(string name)
    {
        var actor = SeedUser("admin");
        var sut = BuildAdminSut(actor);

        sut.NewShareName = name;
        var ok = await sut.CreateShareAsync();

        Assert.False(ok);
        Assert.NotNull(sut.CreateErrorMessage);
        await using var db = NewContext();
        Assert.Empty(await db.ShareDefinitions.ToListAsync());
        Assert.False(Directory.Exists(Path.Combine(_storagePath, name)));
    }

    [Fact]
    public async Task CreateShareAsync_WithDuplicateName_IsRejected()
    {
        var actor = SeedUser("admin");
        SeedPoolShare("docs");
        var sut = BuildAdminSut(actor);

        sut.NewShareName = "docs";
        var ok = await sut.CreateShareAsync();

        Assert.False(ok);
        await using var db = NewContext();
        Assert.Equal(1, await db.ShareDefinitions.CountAsync());
    }

    [Fact]
    public async Task CreateShareAsync_PersistsRootMetadataWithOwnerFullControlAcl()
    {
        var actor = SeedUser("admin");
        var sut = BuildAdminSut(actor);

        sut.NewShareName = "docs";
        var ok = await sut.CreateShareAsync();

        Assert.True(ok);

        await using var db = NewContext();
        var share = await db.ShareDefinitions.SingleAsync(s => s.Name == "docs");

        // The root FileMetadata must exist and use the "" convention (not "/"),
        // otherwise the ACL resolver never finds the owner entry.
        var rootMeta = await db.FileMetadata
            .Include(m => m.Acl)
            .SingleAsync(m => m.ShareId == share.Id && m.Path == "");

        Assert.True(rootMeta.IsDirectory);
        Assert.Equal(actor.Id, rootMeta.OwnerId);

        // The owner ACL must actually be persisted and linked to the root metadata.
        var ownerAcl = Assert.Single(rootMeta.Acl, a => a.PrincipalId == actor.Id);
        Assert.Equal(actor.Id, ownerAcl.PrincipalId);
        Assert.Equal(AclEntryType.Allow, ownerAcl.EntryType);
        Assert.Equal(FilePermission.FullControl, ownerAcl.Permissions & FilePermission.FullControl);
        Assert.Contains(rootMeta.Acl, a =>
            a.PrincipalId == WellKnownGUIDs.ROLE_ADMIN
            && a.EntryType == AclEntryType.Allow
            && (a.Permissions & FilePermission.FullControl) == FilePermission.FullControl);
        CleanupPools();
    }



    [Fact]
    public async Task ChangeStoragePoolAsync_MovesDataAndPersistsNewSharePath()
    {
        var actor = SeedUser("admin");
        var source = Path.Combine(_storagePools[0], "docs");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "important.txt"), "content");
        var seeded = SeedShare("docs", source);
        var (sut, _) = await LoadAndSelectAsync(seeded, actor);
        sut.EditSharePoolPath = _storagePools[1];

        var ok = await sut.ChangeStoragePoolAsync();

        Assert.True(ok);
        var destination = Path.Combine(_storagePools[1], "docs");
        Assert.False(Directory.Exists(source));
        Assert.Equal("content", await File.ReadAllTextAsync(
            Path.Combine(destination, "important.txt")));

        await using var db = NewContext();
        Assert.Equal(destination, (await db.ShareDefinitions.FindAsync(seeded.Id))!.Path);
        CleanupPools();
    }

    [Fact]
    public async Task CreateShareAsync_WithoutResolvableCreator_PersistsNothing()
    {
        // Actor is authenticated (name claim present) but has no matching user row,
        // so the creator cannot be resolved. No orphaned share may be left behind.
        _mgmtAuth
            .Setup(m => m.HasAnyPermissionAsync(It.IsAny<UserContext>(), ManagementPermission.CreateShares))
            .ReturnsAsync(true);

        var sut = new ShareListViewModel(
            ShareRepo(), UserRepo(), GroupRepo(), AclRepo(), FileMetadataRepo(),
            _aclService.Object, _mgmtAuth.Object,
            UserContextFactoryFor().Object,
            _lockManager,
            AuthStateFor("ghost"),
            NullLogger<ShareListViewModel>.Instance,
            _storagePools);

        sut.NewShareName = "docs";
        var ok = await sut.CreateShareAsync();

        Assert.False(ok);
        await using var db = NewContext();
        Assert.Empty(await db.ShareDefinitions.ToListAsync());
        Assert.Empty(await db.FileMetadata.ToListAsync());
    }

    // ─────────────────────── Load scoping ───────────────────────

    [Fact]
    public async Task LoadAsync_AsGlobalAdmin_SeesEvenHiddenAndDisabledShares()
    {
        var actor = SeedUser("admin");
        SeedPoolShare("enabled", isEnabled: true);
        SeedPoolShare("hidden", isHidden: true);
        SeedPoolShare("disabled", isEnabled: false);

        var sut = BuildAdminSut(actor);
        await sut.LoadAsync();

        Assert.Equal(3, sut.Shares.Count); // management scope ignores hidden/disabled
        Assert.True(sut.IsAdmin);
    }
}
