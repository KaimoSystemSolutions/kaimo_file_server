using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

public partial class UserListViewModelDatabaseTests
{
    // ═══════════════════ Own security settings are locked ═══════════════════

    [Fact]
    public async Task SaveUserAsync_SoleAdminDisablesOwnAccount_StaysEnabled()
    {
        var admin = SeedUser("admin");
        var adminRole = SeedAdminRole();
        SeedGlobalAssignment(admin.Id, adminRole.Id);

        var sut = BuildGlobalAdminSut(admin);
        await sut.LoadAsync();
        await sut.SelectUserAsync(sut.Users.Single(u => u.Id == admin.Id));
        await sut.StartEditUserAsync();

        sut.EditUserIsEnabled = false; // disabling the last admin locks everyone out
        await sut.SaveUserAsync();

        await using var db = NewContext();
        Assert.True((await db.Users.FindAsync(admin.Id))!.IsEnabled);
    }

    [Fact]
    public async Task SaveUserAsync_AdminEditsSelf_SecurityTogglesAreIgnored()
    {
        // An admin must not disable their own account or revoke their own password change,
        // even when another admin exists (the last-admin guard would not catch that).
        var admin = SeedUser("admin");
        var admin2 = SeedUser("admin2");
        var adminRole = SeedAdminRole();
        SeedGlobalAssignment(admin.Id, adminRole.Id);
        SeedGlobalAssignment(admin2.Id, adminRole.Id);

        var sut = BuildGlobalAdminSut(admin);
        await sut.LoadAsync();
        await sut.SelectUserAsync(sut.Users.Single(u => u.Id == admin.Id));
        await sut.StartEditUserAsync();

        sut.EditUserIsEnabled = false;
        sut.EditUserCanChangePassword = false;
        sut.EditUserDescription = "changed";
        await sut.SaveUserAsync();

        Assert.Null(sut.ErrorMessage);
        await using var db = NewContext();
        var row = (await db.Users.FindAsync(admin.Id))!;
        Assert.True(row.IsEnabled);
        Assert.True(row.CanChangePassword);
        Assert.Equal("changed", row.Description); // the rest of the save still applies
    }

    [Fact]
    public async Task SaveUserAsync_AdminEditsSelf_KeepsConcurrentSecurityChangeByOtherAdmin()
    {
        // Another admin revokes the password-change right while this admin edits their own
        // record: the locked toggles must keep the stored value, not the one loaded earlier.
        var admin = SeedUser("admin");
        var sut = BuildGlobalAdminSut(admin);
        await sut.LoadAsync();
        await sut.SelectUserAsync(sut.Users.Single(u => u.Id == admin.Id));
        await sut.StartEditUserAsync();

        await using (var other = NewContext())
        {
            (await other.Users.FindAsync(admin.Id))!.CanChangePassword = false;
            await other.SaveChangesAsync();
        }

        sut.EditUserDescription = "changed";
        await sut.SaveUserAsync();

        await using var db = NewContext();
        var row = (await db.Users.FindAsync(admin.Id))!;
        Assert.False(row.CanChangePassword);
        Assert.Equal("changed", row.Description);
    }

    [Fact]
    public async Task SaveUserAsync_AdminEditsSelf_HomeFolderToggleApplies()
    {
        // The home folder is not a sign-in setting (it is switchable in Settings → Home folders too),
        // so it stays editable on one's own account.
        var admin = SeedUser("admin");
        var sut = BuildGlobalAdminSut(admin, HomesService());
        await sut.LoadAsync();
        await sut.SelectUserAsync(sut.Users.Single(u => u.Id == admin.Id));
        await sut.StartEditUserAsync();
        Assert.True(sut.CanManageHomes);

        sut.EditUserHomeDirectoryEnabled = false;
        await sut.SaveUserAsync();

        await using var db = NewContext();
        Assert.False((await db.Users.FindAsync(admin.Id))!.HomeDirectoryEnabled);
    }

    [Fact]
    public async Task SaveUserAsync_AdminEditsOtherUser_SecurityTogglesApply()
    {
        var admin = SeedUser("admin");
        var target = SeedUser("bob");

        var sut = BuildGlobalAdminSut(admin);
        await sut.LoadAsync();
        await sut.SelectUserAsync(sut.Users.Single(u => u.Id == target.Id));
        await sut.StartEditUserAsync();

        sut.EditUserCanChangePassword = false;
        await sut.SaveUserAsync();

        await using var db = NewContext();
        Assert.False((await db.Users.FindAsync(target.Id))!.CanChangePassword);
    }

    [Fact]
    public async Task CanEditSecuritySettingsForSelected_FalseForSelf_TrueForOthers()
    {
        var admin = SeedUser("admin");
        var target = SeedUser("bob");

        var sut = BuildGlobalAdminSut(admin);
        await sut.LoadAsync();
        await sut.SelectUserAsync(sut.Users.Single(u => u.Id == admin.Id));
        Assert.False(sut.CanEditSecuritySettingsForSelected);

        await sut.SelectUserAsync(sut.Users.Single(u => u.Id == target.Id));
        Assert.True(sut.CanEditSecuritySettingsForSelected);
    }

    /// <summary>A home-folder service over the real share/user tables with an enabled homes
    /// share; file-level collaborators are stubs (disabling a home only touches metadata/ACLs).</summary>
    private HomeDirectoryService HomesService()
    {
        using (var db = NewContext())
        {
            db.ShareDefinitions.Add(new ShareDefinition("users", "/data/storage/pool01/users") { IsUserHomes = true });
            db.SaveChanges();
        }

        var metadata = new Mock<IFileMetadataRepository>();
        metadata.Setup(r => r.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<Guid>(), It.IsAny<Guid>()))
            .ReturnsAsync((string path, bool isDir, Guid owner, Guid shareId) =>
                new FileMetadata { Id = Guid.NewGuid(), ShareId = shareId, Path = path, IsDirectory = isDir, OwnerId = owner });
        var acls = new Mock<IAclRepository>();
        acls.Setup(r => r.GetByFileMetadataIdAsync(It.IsAny<Guid>())).ReturnsAsync([]);

        return new HomeDirectoryService(
            ShareRepo(), UserRepo(), metadata.Object, acls.Object,
            Mock.Of<IFileVersionService>(), Mock.Of<IShareLinkRepository>(), Mock.Of<IFileChangeLogRepository>(),
            NullLogger<HomeDirectoryService>.Instance);
    }
}
