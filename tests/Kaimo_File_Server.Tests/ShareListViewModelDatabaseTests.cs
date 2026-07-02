using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
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
    private readonly Mock<IStorageEngine> _storage = new();
    private readonly ShareLockManager _lockManager = new();
    private readonly string _storagePath;

    public ShareListViewModelDatabaseTests()
    {
        _storagePath = Path.Combine(Path.GetTempPath(), "kaimo-tests", Guid.NewGuid().ToString("N"));
        _storage.Setup(s => s.CreateDirectoryAsync(It.IsAny<string>())).Returns(Task.CompletedTask);
    }

    /// <summary>
    /// Builds the SUT for an actor who is an unrestricted (Global) share admin — the
    /// common case for exercising the management mutations.
    /// </summary>
    private ShareListViewModel BuildAdminSut(User actor)
    {
        var ctx = ContextFor(actor);

        _mgmtAuth
            .Setup(m => m.GetAuthorizedShareIdsAnyAsync(It.IsAny<UserContext>(), ManagementPermission.ShareAdmin))
            .ReturnsAsync(AuthorizedScopeResult.Unrestricted());
        _mgmtAuth
            .Setup(m => m.HasAnyPermissionAsync(It.IsAny<UserContext>(), ManagementPermission.CreateShares))
            .ReturnsAsync(true);

        return new ShareListViewModel(
            ShareRepo(), UserRepo(), GroupRepo(), AclRepo(),
            _aclService.Object, _mgmtAuth.Object,
            UserContextFactoryFor((actor.Username, ctx)).Object,
            _storage.Object, _lockManager,
            AuthStateFor(actor.Username),
            NullLogger<ShareListViewModel>.Instance,
            _storagePath);
    }

    private async Task<(ShareListViewModel Sut, ShareDefinition Share)> LoadAndSelectAsync(
        ShareDefinition seeded, User actor)
    {
        var sut = BuildAdminSut(actor);
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
        var seeded = SeedShare("docs", isEnabled: true);
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
        var seeded = SeedShare("docs", isRecycleEnabled: false);
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
        var seeded = SeedShare("docs", isHidden: false);
        var (sut, _) = await LoadAndSelectAsync(seeded, actor);

        var ok = await sut.ToggleShareHiddenAsync();

        Assert.True(ok);
        await using var db = NewContext();
        var row = await db.ShareDefinitions.FindAsync(seeded.Id);
        Assert.True(row!.IsShareHidden);
    }

    [Fact]
    public async Task Toggle_WithNoSelection_DoesNothing()
    {
        var actor = SeedUser("admin");
        SeedShare("docs", isEnabled: true);
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
        var seeded = SeedShare("oldname");
        var (sut, _) = await LoadAndSelectAsync(seeded, actor);

        sut.EditShareName = "newname";
        var ok = await sut.RenameShareAsync();

        Assert.True(ok);
        await using var db = NewContext();
        var row = await db.ShareDefinitions.FindAsync(seeded.Id);
        Assert.Equal("newname", row!.Name);
        Assert.EndsWith("/newname", row.Path); // path rebuilt from the new name
    }

    [Fact]
    public async Task RenameShareAsync_ToExistingName_IsRejectedAndDoesNotPersist()
    {
        var actor = SeedUser("admin");
        var seeded = SeedShare("oldname");
        SeedShare("taken");
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
        var seeded = SeedShare("oldname");
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
        var seeded = SeedShare("docs");
        var (sut, _) = await LoadAndSelectAsync(seeded, actor);

        var ok = await sut.DeleteShareAsync();

        Assert.True(ok);
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
        _storage.Verify(s => s.CreateDirectoryAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CreateShareAsync_WithDuplicateName_IsRejected()
    {
        var actor = SeedUser("admin");
        SeedShare("docs");
        var sut = BuildAdminSut(actor);

        sut.NewShareName = "docs";
        var ok = await sut.CreateShareAsync();

        Assert.False(ok);
        await using var db = NewContext();
        Assert.Equal(1, await db.ShareDefinitions.CountAsync());
    }

    // ─────────────────────── Load scoping ───────────────────────

    [Fact]
    public async Task LoadAsync_AsGlobalAdmin_SeesEvenHiddenAndDisabledShares()
    {
        var actor = SeedUser("admin");
        SeedShare("enabled", isEnabled: true);
        SeedShare("hidden", isHidden: true);
        SeedShare("disabled", isEnabled: false);

        var sut = BuildAdminSut(actor);
        await sut.LoadAsync();

        Assert.Equal(3, sut.Shares.Count); // management scope ignores hidden/disabled
        Assert.True(sut.IsAdmin);
    }
}
