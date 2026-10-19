using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Tests.Infrastructure;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

public partial class UserListViewModelDatabaseTests
{
    // ═══════════════════ Save user ═══════════════════

    [Fact]
    public async Task SaveUserAsync_PersistsProfileChanges()
    {
        var actor = SeedUser("admin");
        var target = SeedUser("bob", "Bob", isEnabled: true);
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SelectUserAsync(sut.Users.Single(u => u.Id == target.Id));
        await sut.StartEditUserAsync();

        sut.EditUserName = "Bobby";
        sut.EditUserEmail = "bobby@example.com";
        sut.EditUserIsEnabled = false; // disable the account
        await sut.SaveUserAsync();

        await using var db = NewContext();
        var row = await db.Users.FindAsync(target.Id);
        Assert.Equal("Bobby", row!.Name);
        Assert.Equal("bobby@example.com", row.Email);
        Assert.False(row.IsEnabled);
    }

    [Fact]
    public async Task SaveUserAsync_WithNewPassword_PersistsNewHash()
    {
        var actor = SeedUser("admin");
        var target = SeedUser("bob");
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SelectUserAsync(sut.Users.Single(u => u.Id == target.Id));
        await sut.StartEditUserAsync();

        sut.NewPassword = "N3wSecret!2026";
        sut.ConfirmPassword = "N3wSecret!2026";
        await sut.SaveUserAsync();

        await using var db = NewContext();
        var row = await db.Users.FindAsync(target.Id);
        Assert.True(_passwords.VerifyPassword("N3wSecret!2026", row!.PasswordHash));
    }

    // ─────────────── Self-service password change ───────────────

    private UserListViewModel BuildSelfServiceSut(User self, ILoginService login)
    {
        // No management permissions: the user may only edit their own profile.
        _mgmtAuth.Setup(m => m.GetAuthorizedDepartmentIdsAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>()))
            .ReturnsAsync(AuthorizedScopeResult.None());
        _mgmtAuth.Setup(m => m.GetAuthorizedDepartmentIdsAnyAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>()))
            .ReturnsAsync(AuthorizedScopeResult.None());
        return new UserListViewModel(
            UserRepo(), GroupRepo(), RoleRepo(), DepartmentRepo(), ScopedRoleRepo(),
            _passwords, _ntHash.Object, _mgmtAuth.Object,
            UserContextFactoryFor((self.Username, ContextFor(self))).Object,
            AuthStateFor(self.Username),
            NullLogger<UserListViewModel>.Instance,
            ShareRepo(), _config.Object,
            loginService: login,
            client: new ClientConnectionInfo { RemoteAddress = "198.51.100.7" });
    }

    private async Task<UserListViewModel> StartSelfPasswordChangeAsync(User self, ILoginService login)
    {
        var sut = BuildSelfServiceSut(self, login);
        await sut.LoadAsync();
        await sut.SelectUserAsync(sut.Users.Single(u => u.Id == self.Id));
        await sut.StartEditUserAsync();
        sut.CurrentPassword = "current-password";
        sut.NewPassword = "Brand-new-secret-42";
        sut.ConfirmPassword = "Brand-new-secret-42";
        return sut;
    }

    /// <summary>
    /// The "current password" check goes through the login service, so it is throttled
    /// and locked out like a sign-in instead of being an unlimited password oracle.
    /// </summary>
    [Fact]
    public async Task SelfPasswordChange_LockedOutCurrentPasswordCheck_DoesNotChangePassword()
    {
        var self = SeedUser("carol");
        var login = new Mock<ILoginService>();
        login.Setup(l => l.AuthenticateAsync("carol", "current-password", "198.51.100.7", It.IsAny<string?>()))
            .ReturnsAsync(LoginResult.LockedOut(TimeSpan.FromMinutes(7)));
        var sut = await StartSelfPasswordChangeAsync(self, login.Object);

        await sut.SaveUserAsync();

        Assert.Contains("7", sut.ErrorMessage);
        await using var db = NewContext();
        Assert.Equal("pw-hash", (await db.Users.FindAsync(self.Id))!.PasswordHash);
        login.Verify(l => l.AuthenticateAsync("carol", "current-password", "198.51.100.7", It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task SelfPasswordChange_Verified_ChangesPassword_AndClearsMustChange()
    {
        var self = SeedUser("carol");
        await using (var db = NewContext())
        {
            var row = await db.Users.FindAsync(self.Id);
            row!.MustChangePassword = true;
            await db.SaveChangesAsync();
        }
        var login = new Mock<ILoginService>();
        login.Setup(l => l.AuthenticateAsync("carol", "current-password", It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(LoginResult.ForSuccess(ContextFor(self)));
        var sut = await StartSelfPasswordChangeAsync(self, login.Object);

        await sut.SaveUserAsync();

        Assert.Null(sut.ErrorMessage);
        await using var check = NewContext();
        var updated = (await check.Users.FindAsync(self.Id))!;
        Assert.True(_passwords.VerifyPassword("Brand-new-secret-42", updated.PasswordHash));
        Assert.False(updated.MustChangePassword);
        Assert.NotEqual(self.SecurityStamp, updated.SecurityStamp);
    }

    [Fact]
    public async Task SaveUserAsync_MismatchedPassword_DoesNotChangeHash()
    {
        var actor = SeedUser("admin");
        var target = SeedUser("bob");
        var originalHash = (await NewContext().Users.FindAsync(target.Id))!.PasswordHash;
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SelectUserAsync(sut.Users.Single(u => u.Id == target.Id));
        await sut.StartEditUserAsync();

        sut.NewPassword = "N3wSecret!2026";
        sut.ConfirmPassword = "different";
        await sut.SaveUserAsync();

        Assert.NotNull(sut.ErrorMessage);
        await using var db = NewContext();
        Assert.Equal(originalHash, (await db.Users.FindAsync(target.Id))!.PasswordHash);
    }

    [Fact]
    public async Task SaveUserAsync_KeepsSystemWideRoleAssignments()
    {
        // Roles are no longer edited in the user form; a profile save must leave the
        // user's system-wide (Global department) assignments untouched.
        var actor = SeedUser("admin");
        var target = SeedUser("bob");
        var role = SeedRole("Reviewer");
        var assignment = SeedGlobalAssignment(target.Id, role.Id);
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SelectUserAsync(sut.Users.Single(u => u.Id == target.Id));
        await sut.StartEditUserAsync();

        sut.EditUserDescription = "changed";
        await sut.SaveUserAsync();

        await using var db = NewContext();
        Assert.True(await db.ScopedRoleAssignments.AnyAsync(a => a.Id == assignment.Id));
    }

    [Fact]
    public async Task CreateAssignmentAsync_GlobalDepartmentScope_IsStoredSystemWideAndListedAsGlobal()
    {
        var actor = SeedUser("admin");
        var role = SeedRole("Delegate");
        var target = SeedUser("bob");
        SeedDepartment("Global", id: WellKnownGUIDs.DEPARTMENT_GLOBAL);
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SelectUserAsync(sut.Users.Single(u => u.Id == target.Id));

        sut.StartAddAssignment();
        sut.NewAssignmentRoleId = role.Id;
        sut.NewAssignmentPrincipalId = target.Id;
        sut.NewAssignmentScopeType = ScopeType.Department;
        sut.NewAssignmentScopeId = WellKnownGUIDs.DEPARTMENT_GLOBAL;
        await sut.CreateAssignmentAsync();

        Assert.Null(sut.ErrorMessage);
        await using var db = NewContext();
        Assert.True(await db.ScopedRoleAssignments.AnyAsync(a => a.RoleId == role.Id
            && a.PrincipalId == target.Id && a.ScopeType == ScopeType.Global));
        Assert.Contains(sut.UserScopedAssignments, a => a.RoleName == "Delegate" && a.ScopeName == "Global");
    }

    [Fact]
    public void StartAddAssignment_DefaultsToDepartmentScope()
    {
        var sut = BuildGlobalAdminSut(SeedUser("admin"));

        sut.StartAddAssignment();

        Assert.Equal(ScopeType.Department, sut.NewAssignmentScopeType);
    }
}
