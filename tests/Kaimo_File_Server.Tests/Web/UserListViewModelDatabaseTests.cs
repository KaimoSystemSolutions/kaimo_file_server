using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Infrastructure.Services;
using Kaimo_File_Server.Tests.Infrastructure;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Regression tests pinning <see cref="UserListViewModel"/> mutations to real database
/// state. This is the widest-surface view model (users, groups, roles, scoped
/// assignments, password resets), so its methods are the most valuable to lock down:
/// a create that doesn't persist, a profile save that drops a field, a password reset
/// that stores the old hash, or a role permission edit that never saves are exactly
/// the kind of silent regressions this suite is built to catch.
///
/// A real <see cref="PasswordService"/> is used so password mutations are verified the
/// way production authenticates — by hashing and re-verifying, not by string equality.
/// </summary>
public partial class UserListViewModelDatabaseTests : DatabaseTestBase
{
    private readonly Mock<IManagementAuthService> _mgmtAuth = new();
    private readonly PasswordService _passwords = new();
    private readonly Mock<INtHashProtector> _ntHash = new();
    private readonly Mock<IConfigRepository> _config = new();

    public UserListViewModelDatabaseTests()
    {
        // Passthrough protector — the encryption round-trip is covered by its own suite.
        _ntHash.Setup(p => p.Protect(It.IsAny<string>())).Returns<string>(s => s);
        _ntHash.Setup(p => p.Unprotect(It.IsAny<string>())).Returns<string>(s => s);
        // Default password policy (min length 6, no character classes).
        _config.Setup(c => c.GetAsync(PasswordPolicy.ConfigKey, It.IsAny<PasswordPolicy>()))
            .ReturnsAsync(PasswordPolicy.Default());
    }

    private UserListViewModel BuildGlobalAdminSut(User actor, HomeDirectoryService? homes = null)
    {
        var ctx = ContextFor(actor);

        _mgmtAuth.Setup(m => m.HasGlobalPermissionAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>()))
            .ReturnsAsync(true);
        _mgmtAuth.Setup(m => m.HasAnyPermissionAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>()))
            .ReturnsAsync(true);
        _mgmtAuth.Setup(m => m.CanManageUserAsync(It.IsAny<UserContext>(), It.IsAny<Guid>(), It.IsAny<ManagementPermission>()))
            .ReturnsAsync(true);
        _mgmtAuth.Setup(m => m.CanManageGroupAsync(It.IsAny<UserContext>(), It.IsAny<Guid>(), It.IsAny<ManagementPermission>()))
            .ReturnsAsync(true);
        _mgmtAuth.Setup(m => m.CanCreateUserInDepartmentAsync(It.IsAny<UserContext>(), It.IsAny<Guid>()))
            .ReturnsAsync(true);
        _mgmtAuth.Setup(m => m.GetAuthorizedDepartmentIdsAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>()))
            .ReturnsAsync(AuthorizedScopeResult.Unrestricted());
        // A global admin holds every bit everywhere and can delegate any role.
        _mgmtAuth.Setup(m => m.GetEffectivePermissionsAtAsync(It.IsAny<UserContext>(), It.IsAny<ScopeType>(), It.IsAny<Guid>()))
            .ReturnsAsync(ManagementPermission.FullAdmin);
        _mgmtAuth.Setup(m => m.CanAssignRoleAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>(), It.IsAny<ScopeType>(), It.IsAny<Guid>()))
            .ReturnsAsync(true);

        return new UserListViewModel(
            UserRepo(), GroupRepo(), RoleRepo(), DepartmentRepo(), ScopedRoleRepo(),
            _passwords, _ntHash.Object, _mgmtAuth.Object,
            UserContextFactoryFor((actor.Username, ctx)).Object,
            AuthStateFor(actor.Username),
            NullLogger<UserListViewModel>.Instance,
            ShareRepo(), _config.Object, homes: homes);
    }

    private Group SeedGroup(string name)
    {
        var group = new Group(Guid.NewGuid(), name);
        using var db = NewContext();
        db.Groups.Add(group);
        db.SaveChanges();
        return group;
    }

    private Role SeedRole(string name, ManagementPermission perms = ManagementPermission.None, bool system = false)
    {
        var role = new Role(Guid.NewGuid(), name, perms, system);
        using var db = NewContext();
        db.Roles.Add(role);
        db.SaveChanges();
        return role;
    }

    // ═══════════════════ Create user ═══════════════════

    [Fact]
    public async Task CreateUserAsync_PersistsUserWithProfileAndPassword()
    {
        var actor = SeedUser("admin");
        var dept = SeedDepartment("Sales");
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();

        sut.CreateUserName = "Charlie";
        sut.CreateUserUsername = "charlie";
        sut.CreateUserPassword = "Passw0rd!Long";
        sut.CreateUserEmail = "charlie@example.com";
        sut.CreateUserIsEnabled = true;
        sut.CreateUserDepartmentId = dept.Id;
        await sut.CreateUserAsync();

        await using var db = NewContext();
        var row = await db.Users.SingleOrDefaultAsync(u => u.Username == "charlie");
        Assert.NotNull(row);
        Assert.Equal("Charlie", row!.Name);
        Assert.Equal("charlie@example.com", row.Email);
        Assert.True(_passwords.VerifyPassword("Passw0rd!Long", row.PasswordHash)); // real hash stored
        // And the user was attached to the chosen department.
        Assert.True(await db.DepartmentUsers.AnyAsync(du => du.UserId == row.Id && du.DepartmentId == dept.Id));
    }

    [Fact]
    public async Task CreateUserAsync_DuplicateUsername_IsRejected()
    {
        var actor = SeedUser("admin");
        var dept = SeedDepartment("Sales");
        SeedUser("charlie");
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();

        sut.CreateUserName = "Charlie";
        sut.CreateUserUsername = "charlie";
        sut.CreateUserPassword = "Passw0rd!Long";
        sut.CreateUserDepartmentId = dept.Id;
        await sut.CreateUserAsync();

        Assert.NotNull(sut.ErrorMessage);
        await using var db = NewContext();
        Assert.Equal(1, await db.Users.CountAsync(u => u.Username == "charlie"));
    }

    [Fact]
    public async Task CreateUserAsync_TooShortPassword_PersistsNothing()
    {
        var actor = SeedUser("admin");
        var dept = SeedDepartment("Sales");
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();

        sut.CreateUserName = "Charlie";
        sut.CreateUserUsername = "charlie";
        sut.CreateUserPassword = "abc"; // below the default min length 12
        sut.CreateUserDepartmentId = dept.Id;
        await sut.CreateUserAsync();

        Assert.NotNull(sut.ErrorMessage);
        await using var db = NewContext();
        Assert.False(await db.Users.AnyAsync(u => u.Username == "charlie"));
    }

    // ═══════════════════ Create group / role ═══════════════════

    [Fact]
    public async Task CreateGroupAsync_PersistsGroup()
    {
        var actor = SeedUser("admin");
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();

        sut.CreateGroupName = "Marketing";
        await sut.CreateGroupAsync();

        await using var db = NewContext();
        Assert.True(await db.Groups.AnyAsync(g => g.Name == "Marketing"));
    }

    [Fact]
    public async Task CreateRoleAsync_PersistsRole()
    {
        var actor = SeedUser("admin");
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();

        sut.CreateRoleName = "Auditor";
        await sut.CreateRoleAsync();

        await using var db = NewContext();
        Assert.True(await db.Roles.AnyAsync(r => r.Name == "Auditor"));
    }
}
