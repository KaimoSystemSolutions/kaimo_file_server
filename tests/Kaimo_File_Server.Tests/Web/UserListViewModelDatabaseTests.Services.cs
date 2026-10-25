using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Web.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

public partial class UserListViewModelDatabaseTests
{
    // ═══════════════════ File services (Services tab) ═══════════════════

    private async Task<Kaimo_File_Server.Web.Components.ViewModels.UserListViewModel> EditAsync(User actor, User target)
    {
        var sut = BuildGlobalAdminSut(actor, serviceAccess: TestGates.AllGloballyOn(UserRepo()));
        await sut.LoadAsync();
        await sut.SelectUserAsync(sut.Users.Single(u => u.Id == target.Id));
        await sut.StartEditUserAsync();
        return sut;
    }

    [Fact]
    public async Task SaveUserAsync_OtherUsersServices_ArePersisted()
    {
        var admin = SeedUser("admin");
        var bob = SeedUser("bob");
        var sut = await EditAsync(admin, bob);
        Assert.True(sut.EditUserHasService(UserServiceAccess.Smb));

        sut.SetEditUserService(UserServiceAccess.Smb, false);
        sut.SetEditUserService(UserServiceAccess.WebDav, false);
        await sut.SaveUserAsync();

        Assert.Null(sut.ErrorMessage);
        await using var db = NewContext();
        Assert.Equal(UserServiceAccess.App | UserServiceAccess.Web,
            (await db.Users.FindAsync(bob.Id))!.ServiceAccess);
    }

    [Fact]
    public async Task SaveUserAsync_AdminEditsSelf_ServicesAreIgnored()
    {
        var admin = SeedUser("admin");
        var sut = await EditAsync(admin, admin);

        sut.SetEditUserService(UserServiceAccess.App, false);
        await sut.SaveUserAsync();

        await using var db = NewContext();
        Assert.Equal(UserServiceAccess.All, (await db.Users.FindAsync(admin.Id))!.ServiceAccess);
    }

    [Fact]
    public async Task SaveUserAsync_MemberOfAdmins_KeepsWebLogin()
    {
        var admin = SeedUser("admin");
        var bob = SeedUser("bob");
        SeedGroupWithId(WellKnownGUIDs.GROUP_ADMINS, "Admins");
        SeedGroupMember(WellKnownGUIDs.GROUP_ADMINS, bob.Id);
        var sut = await EditAsync(admin, bob);
        Assert.True(sut.SelectedUserKeepsWebLogin);

        sut.SetEditUserService(UserServiceAccess.Web, false);
        sut.SetEditUserService(UserServiceAccess.Smb, false);
        await sut.SaveUserAsync();

        await using var db = NewContext();
        Assert.Equal(UserServiceAccess.All & ~UserServiceAccess.Smb,
            (await db.Users.FindAsync(bob.Id))!.ServiceAccess);
    }

    [Fact]
    public async Task StartEditUserAsync_ShowsWhichServicesAreOffGlobally()
    {
        var admin = SeedUser("admin");
        _config.Setup(c => c.GetBoolAsync(ServiceAccessGate.GlobalKey(UserServiceAccess.Smb), It.IsAny<bool>()))
            .ReturnsAsync(false);
        _config.Setup(c => c.GetBoolAsync(ServiceAccessGate.GlobalKey(UserServiceAccess.App), It.IsAny<bool>()))
            .ReturnsAsync(true);
        var sut = BuildGlobalAdminSut(admin, serviceAccess: new ServiceAccessGate(_config.Object, UserRepo()));
        await sut.LoadAsync();
        await sut.SelectUserAsync(sut.Users.Single(u => u.Id == admin.Id));
        await sut.StartEditUserAsync();

        Assert.True(sut.IsServiceOffGlobally(UserServiceAccess.Smb));
        Assert.False(sut.IsServiceOffGlobally(UserServiceAccess.App));
    }

    [Fact]
    public async Task CreateUserAsync_AppliesTheDefaultServices()
    {
        var actor = SeedUser("admin");
        _config.Setup(c => c.GetIntAsync(ServiceAccessGate.DefaultAccessKey, It.IsAny<int>()))
            .ReturnsAsync((int)(UserServiceAccess.App | UserServiceAccess.Web));
        var sut = BuildGlobalAdminSut(actor, serviceAccess: new ServiceAccessGate(_config.Object, UserRepo()));
        await sut.LoadAsync();

        sut.StartCreateUser();
        sut.CreateUserName = "Dora";
        sut.CreateUserUsername = "dora";
        sut.CreateUserPassword = "Passw0rd!Long";
        sut.CreateUserDepartmentId = WellKnownGUIDs.DEPARTMENT_GLOBAL;
        await sut.CreateUserAsync();

        await using var db = NewContext();
        var row = await db.Users.SingleAsync(u => u.Username == "dora");
        Assert.Equal(UserServiceAccess.App | UserServiceAccess.Web, row.ServiceAccess);
    }
}
