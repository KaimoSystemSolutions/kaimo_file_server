using System.Security.Claims;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories.Identity;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Security.Authentication;
using Kaimo_File_Server.Core.Security.Management;
using Kaimo_File_Server.Core.Services.Management;
using Kaimo_File_Server.Infrastructure.Repositories;
using Kaimo_File_Server.Web.Components.ViewModels;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.ViewModels.Users;

/// <summary>
/// User details → SMB lockout (AP8e): shows an SMB lock recorded by the bridge and lets an
/// administrator with EnableDisableUsers on the user request an unlock, which the Samba user
/// sync applies with its next cycle. Never for one's own account; re-checked on the click.
/// Runs on the real EF schema (in-memory SQLite).
/// </summary>
public sealed class SmbLockViewModelTests : DatabaseTestBase
{
    private readonly MutableTimeProvider _time = new();
    private readonly User _admin = new(Guid.NewGuid(), "Admin", "admin", "pw", "nt");
    private readonly User _anna = new(Guid.NewGuid(), "Anna", "anna.weber", "pw", "nt");
    private readonly Mock<IManagementAuthService> _mgmtAuth = new();
    private readonly SecurityEventRepository _events;
    private readonly UserRepository _users;

    public SmbLockViewModelTests()
    {
        _events = new SecurityEventRepository(DbFactory);
        _users = UserRepo();
        using var db = NewContext();
        db.Users.AddRange(_admin, _anna);
        db.SaveChanges();
        SetPermission(true);
    }

    private DateTime Now => _time.Now.UtcDateTime;

    private void SetPermission(bool allowed)
        => _mgmtAuth.Setup(m => m.CanManageUserAsync(It.IsAny<UserContext>(), It.IsAny<Guid>(), ManagementPermission.EnableDisableUsers))
            .ReturnsAsync(allowed);

    private SmbLockViewModel Create()
    {
        var authState = new Mock<AuthenticationStateProvider>();
        authState.Setup(a => a.GetAuthenticationStateAsync()).ReturnsAsync(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], "test"))));
        var contexts = new Mock<IUserContextFactory>();
        contexts.Setup(c => c.CreateByUsernameAsync("admin")).ReturnsAsync(new UserContext(_admin, [], [], []));
        return new SmbLockViewModel(authState.Object, contexts.Object, _mgmtAuth.Object, _users, _events,
            NullLogger<SmbLockViewModel>.Instance, _time);
    }

    private Task LockAsync(SecurityChannel channel, DateTime at, DateTime until, string user = "anna.weber")
        => _events.SaveAsync([new LoginAttemptRecord
        {
            AtUtc = at, Channel = channel, Username = user, Address = "10.0.0.5",
            Outcome = LoginOutcome.LockedOut, LockedUntilUtc = until,
        }], []);

    private async Task<User> ReloadAnnaAsync() => (await _users.GetByIdAsync(_anna.Id))!;

    [Fact]
    public async Task Load_ShowsTheActiveSmbLock()
    {
        await LockAsync(SecurityChannel.Smb, Now.AddMinutes(-2), Now.AddMinutes(13));
        var vm = Create();

        await vm.LoadAsync(_anna);

        Assert.True(vm.CanUnlock);
        Assert.Equal(Now.AddMinutes(13), vm.LockedUntilUtc);
    }

    [Fact]
    public async Task Load_IgnoresWebLocksEndedLocksAndOtherAccounts()
    {
        await LockAsync(SecurityChannel.Web, Now.AddMinutes(-2), Now.AddMinutes(13));
        await LockAsync(SecurityChannel.Smb, Now.AddMinutes(-30), Now.AddMinutes(-15));
        await LockAsync(SecurityChannel.Smb, Now.AddMinutes(-2), Now.AddMinutes(13), user: "admin");
        var vm = Create();

        await vm.LoadAsync(_anna);

        Assert.Null(vm.LockedUntilUtc);
    }

    [Fact]
    public async Task Unlock_StoresTheRequestForTheSambaSync_AndHidesTheLock()
    {
        await LockAsync(SecurityChannel.Smb, Now.AddMinutes(-2), Now.AddMinutes(13));
        var vm = Create();
        await vm.LoadAsync(_anna);

        await vm.UnlockAsync();

        Assert.Null(vm.ErrorMessage);
        Assert.True(vm.UnlockRequested);
        Assert.Null(vm.LockedUntilUtc);
        var anna = await ReloadAnnaAsync();
        Assert.Equal(Now, anna.SmbUnlockRequestedAtUtc);
        var source = Assert.Single(await _users.GetSambaCredentialBatchAsync(0, 10, CancellationToken.None),
            s => s.Username == "anna.weber");
        Assert.Equal(Now, source.SmbUnlockRequestedAtUtc);

        // The recorded lock ends with the request, so the overview no longer counts it.
        Assert.Equal(0, (await _events.GetTotalsAsync(Now.AddDays(-1), Now)).ActiveLockouts);

        // The lock that existed before the request is resolved; it stays hidden after a reload.
        var reloaded = Create();
        await reloaded.LoadAsync(anna);
        Assert.Null(reloaded.LockedUntilUtc);
    }

    [Fact]
    public async Task ALockAfterTheUnlockRequest_IsShownAgain()
    {
        var vm = Create();
        await vm.LoadAsync(_anna);
        await vm.UnlockAsync();
        _time.Advance(TimeSpan.FromMinutes(5));
        await LockAsync(SecurityChannel.Smb, Now, Now.AddMinutes(15));

        var reloaded = Create();
        await reloaded.LoadAsync(await ReloadAnnaAsync());

        Assert.Equal(Now.AddMinutes(15), reloaded.LockedUntilUtc);
    }

    [Fact]
    public async Task Unlock_WithoutPermissionAtClickTime_WritesNothing()
    {
        var vm = Create();
        await vm.LoadAsync(_anna);
        SetPermission(false);

        await vm.UnlockAsync();

        Assert.NotNull(vm.ErrorMessage);
        Assert.False(vm.UnlockRequested);
        Assert.Null((await ReloadAnnaAsync()).SmbUnlockRequestedAtUtc);
    }

    [Fact]
    public async Task WithoutPermission_OrForTheOwnAccount_NoUnlockIsOffered()
    {
        SetPermission(false);
        var vm = Create();
        await vm.LoadAsync(_anna);
        Assert.False(vm.CanUnlock);

        SetPermission(true);
        await vm.LoadAsync(_admin);
        Assert.False(vm.CanUnlock);

        await vm.UnlockAsync();
        Assert.Null((await _users.GetByIdAsync(_admin.Id))!.SmbUnlockRequestedAtUtc);
    }
}
