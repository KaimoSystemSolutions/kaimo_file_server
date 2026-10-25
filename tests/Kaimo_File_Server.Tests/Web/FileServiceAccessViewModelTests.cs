using System.Security.Claims;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>The per-user file-service matrix on the settings page.</summary>
public sealed class FileServiceAccessViewModelTests
{
    private readonly User _actor = new(Guid.NewGuid(), "Admin", "admin", "pw", "nt");
    private readonly User _alice = new(Guid.NewGuid(), "Alice", "alice", "pw", "nt");
    private readonly Mock<IUserRepository> _users = new();
    private readonly Mock<IConfigRepository> _config = new();
    private readonly Mock<IManagementAuthService> _mgmtAuth = new();

    private FileServiceAccessViewModel Create(bool canManage = true)
    {
        var authState = new Mock<AuthenticationStateProvider>();
        authState.Setup(a => a.GetAuthenticationStateAsync()).ReturnsAsync(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], "test"))));
        var actorContext = new UserContext(_actor, [], [], []);
        var contexts = new Mock<IUserContextFactory>();
        contexts.Setup(c => c.CreateByUsernameAsync("admin")).ReturnsAsync(actorContext);
        _mgmtAuth.Setup(m => m.HasGlobalPermissionAsync(actorContext, ManagementPermission.EnableDisableUsers))
            .ReturnsAsync(canManage);

        _users.Setup(u => u.GetAllAsync()).ReturnsAsync([_alice, _actor]);
        var groups = new Mock<IGroupRepository>();
        groups.Setup(g => g.GetMembersAsync(WellKnownGUIDs.GROUP_ADMINS)).ReturnsAsync([_actor]);
        _config.Setup(c => c.GetIntAsync(ServiceAccessGate.DefaultAccessKey, It.IsAny<int>()))
            .ReturnsAsync((int)(UserServiceAccess.App | UserServiceAccess.Web));

        return new FileServiceAccessViewModel(authState.Object, contexts.Object, _mgmtAuth.Object,
            _users.Object, groups.Object, _config.Object, NullLogger<FileServiceAccessViewModel>.Instance);
    }

    [Fact]
    public async Task Load_ListsUsers_MarksAdmins_AndReadsDefault()
    {
        var vm = Create();
        await vm.LoadAsync();

        Assert.Equal(["Admin", "Alice"], vm.Rows.Select(r => r.Name));
        Assert.True(vm.Rows[0].IsAdmin);
        Assert.False(vm.Rows[1].IsAdmin);
        Assert.Equal(UserServiceAccess.App | UserServiceAccess.Web, vm.DefaultAccess);
    }

    [Fact]
    public async Task Load_WithoutPermission_ShowsNothing()
    {
        var vm = Create(canManage: false);
        await vm.LoadAsync();

        Assert.False(vm.CanManage);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task Set_SavesTheUsersFlags()
    {
        var vm = Create();
        await vm.LoadAsync();
        var alice = vm.Rows.Single(r => r.Username == "alice");

        await vm.SetAsync(alice, UserServiceAccess.Smb, false);

        _users.Verify(u => u.UpdateServiceAccessAsync(_alice.Id, UserServiceAccess.All & ~UserServiceAccess.Smb));
        Assert.False(alice.Has(UserServiceAccess.Smb));
        Assert.Equal(1, vm.CountAllowed(UserServiceAccess.Smb));
    }

    [Fact]
    public async Task Set_FailedSave_RevertsTheRow()
    {
        _users.Setup(u => u.UpdateServiceAccessAsync(It.IsAny<Guid>(), It.IsAny<UserServiceAccess>()))
            .ThrowsAsync(new InvalidOperationException("db down"));
        var vm = Create();
        await vm.LoadAsync();
        var alice = vm.Rows.Single(r => r.Username == "alice");

        await vm.SetAsync(alice, UserServiceAccess.App, false);

        Assert.True(alice.Has(UserServiceAccess.App));
        Assert.NotNull(vm.ErrorMessage);
    }

    [Fact]
    public async Task Set_AdminWebLogin_IsLockedOn()
    {
        var vm = Create();
        await vm.LoadAsync();
        var admin = vm.Rows.Single(r => r.IsAdmin);

        await vm.SetAsync(admin, UserServiceAccess.Web, false);

        Assert.True(admin.Has(UserServiceAccess.Web));
        _users.Verify(u => u.UpdateServiceAccessAsync(It.IsAny<Guid>(), It.IsAny<UserServiceAccess>()), Times.Never);
    }

    [Fact]
    public async Task SetDefault_WritesTheConfig_AndLeavesUsersAlone()
    {
        var vm = Create();
        await vm.LoadAsync();

        await vm.SetDefaultAsync(UserServiceAccess.Smb, true);

        _config.Verify(c => c.SetAsync(ServiceAccessGate.DefaultAccessKey,
            (int)(UserServiceAccess.App | UserServiceAccess.Web | UserServiceAccess.Smb)));
        _users.Verify(u => u.UpdateServiceAccessAsync(It.IsAny<Guid>(), It.IsAny<UserServiceAccess>()), Times.Never);
    }
}
