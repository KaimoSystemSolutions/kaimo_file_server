using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.Notifications;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Infrastructure.Repositories;
using Kaimo_File_Server.Infrastructure.Services;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Services;
using Kaimo_File_Server.Web.Services.SecondFactor;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.ViewModels.Users;

/// <summary>
/// User details → two-factor sign-in: self-service set-up (password re-check first), removal and
/// new recovery codes for the own account, and the administrator reset (permission-gated, never
/// for the own account).
/// </summary>
public class UserSecondFactorViewModelTests : DatabaseTestBase
{
    private const string Password = "correct-horse";

    private readonly MutableTimeProvider _time = new() { Now = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero) };
    private readonly IDataProtectionProvider _dataProtection = new EphemeralDataProtectionProvider();
    private readonly LoginThrottle _throttle;
    private readonly ConfigRepository _config;
    private readonly Mock<IManagementAuthService> _mgmt = new();
    private readonly Mock<ILoginService> _login = new();

    public UserSecondFactorViewModelTests()
    {
        _throttle = new LoginThrottle(_time);
        _config = new ConfigRepository(DbFactory, new MemoryCache(new MemoryCacheOptions()));
    }

    private SecondFactorService Service() => new(
        new SecondFactorRepository(DbFactory),
        new SecondFactorPolicyService(_config, new UserContextFactory(DbFactory), DepartmentRepo(), ScopedRoleRepo(),
            RoleRepo(), new SecondFactorRepository(DbFactory), new MemoryCache(new MemoryCacheOptions())),
        _dataProtection, _throttle, _config, Mock.Of<INotificationPublisher>(), new SecurityMonitor(), new SecurityEventRepository(DbFactory),
        NullLogger<SecondFactorService>.Instance, _time);

    private UserSecondFactorViewModel Vm(User actor) => new(
        AuthStateFor(actor.Username), new UserContextFactory(DbFactory), _mgmt.Object, Service(), _login.Object,
        new ClientConnectionInfo(), NullLogger<UserSecondFactorViewModel>.Instance);

    private void ArrangePassword(User user)
        => _login.Setup(l => l.AuthenticateAsync(user.Username, Password, It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(LoginResult.ForSuccess(new UserContext(user, [], [], [])));

    private string Code(byte[] secret) => Totp.Compute(secret, Totp.StepAt(_time.Now));

    private async Task<byte[]> SetUpAsync(UserSecondFactorViewModel vm)
    {
        vm.StartSetup();
        vm.Password = Password;
        await vm.BeginSetupAsync();
        var secret = vm.Enrollment!.Secret;
        vm.Code = Code(secret);
        await vm.ConfirmSetupAsync();
        _time.Advance(TimeSpan.FromSeconds(Totp.StepSeconds));
        return secret;
    }

    [Fact]
    public async Task Self_SetUp_NeedsThePassword_ThenShowsRecoveryCodes()
    {
        var user = SeedUser("erika");
        ArrangePassword(user);
        var vm = Vm(user);
        await vm.LoadAsync(user);

        Assert.True(vm.IsSelf);
        Assert.False(vm.Status!.IsEnrolled);

        vm.StartSetup();
        vm.Password = "wrong";
        await vm.BeginSetupAsync();
        Assert.Null(vm.Enrollment);
        Assert.NotNull(vm.ErrorMessage);

        vm.Password = Password;
        await vm.BeginSetupAsync();
        Assert.NotNull(vm.Enrollment);
        Assert.Equal("", vm.Password);

        vm.Code = Code(vm.Enrollment!.Secret);
        await vm.ConfirmSetupAsync();

        Assert.True(vm.Status!.IsEnrolled);
        Assert.Equal(SecondFactorService.RecoveryCodeCount, vm.RecoveryCodes!.Count);
        Assert.Null(vm.Enrollment);
    }

    [Fact]
    public async Task Self_Disable_WithCode()
    {
        var user = SeedUser("erika");
        ArrangePassword(user);
        var vm = Vm(user);
        await vm.LoadAsync(user);
        var secret = await SetUpAsync(vm);

        vm.StartDisable();
        vm.Code = Code(secret);
        await vm.SubmitCodeAsync();

        Assert.False(vm.Status!.IsEnrolled);
        Assert.Equal(SecondFactorPanelMode.None, vm.Mode);
    }

    [Fact]
    public async Task Self_Disable_IsNotOffered_WhenRequired()
    {
        var user = SeedUser("erika");
        ArrangePassword(user);
        var vm = Vm(user);
        await vm.LoadAsync(user);
        await SetUpAsync(vm);
        await _config.SetAsync(SecondFactorPolicy.ConfigKey,
            new SecondFactorPolicy { Mode = SecondFactorMode.Required, AppliesToAll = true });

        // A fresh circuit: the policy cache of the first one still holds the old policy.
        var later = Vm(user);
        await later.LoadAsync(user);

        Assert.False(later.CanDisable);
        Assert.True(later.CanRegenerate);
    }

    [Fact]
    public async Task Self_RegenerateRecoveryCodes_WithCode()
    {
        var user = SeedUser("erika");
        ArrangePassword(user);
        var vm = Vm(user);
        await vm.LoadAsync(user);
        var secret = await SetUpAsync(vm);
        var first = vm.RecoveryCodes!;
        vm.DismissRecoveryCodes();

        vm.StartRegenerate();
        vm.Code = Code(secret);
        await vm.SubmitCodeAsync();

        Assert.NotNull(vm.RecoveryCodes);
        Assert.Empty(vm.RecoveryCodes!.Intersect(first));
    }

    [Fact]
    public async Task Admin_CanResetAnotherUsersFactor_WithPermission()
    {
        var user = SeedUser("erika");
        ArrangePassword(user);
        var self = Vm(user);
        await self.LoadAsync(user);
        await SetUpAsync(self);

        var admin = SeedUser("admin");
        _mgmt.Setup(m => m.CanManageUserAsync(It.Is<UserContext>(c => c.User.Id == admin.Id), user.Id, ManagementPermission.ResetSecondFactor))
            .ReturnsAsync(true);
        var vm = Vm(admin);
        await vm.LoadAsync(user);

        Assert.False(vm.IsSelf);
        Assert.True(vm.CanReset);
        await vm.ResetAsync();

        Assert.False(vm.Status!.IsEnrolled);
        Assert.Null(await new SecondFactorRepository(DbFactory).GetAsync(user.Id));
    }

    [Fact]
    public async Task Admin_Reset_OfAFactorThatIsAlreadyGone_SaysSo()
    {
        var user = SeedUser("erika");
        ArrangePassword(user);
        var self = Vm(user);
        await self.LoadAsync(user);
        await SetUpAsync(self);

        var admin = SeedUser("admin");
        _mgmt.Setup(m => m.CanManageUserAsync(It.Is<UserContext>(c => c.User.Id == admin.Id), user.Id, ManagementPermission.ResetSecondFactor))
            .ReturnsAsync(true);
        var vm = Vm(admin);
        await vm.LoadAsync(user);
        vm.ConfirmingReset = true;
        await Service().ResetAsync(user, admin.Id); // another administrator was faster

        await vm.ResetAsync();

        var expected = Kaimo_File_Server.Core.Language.Resources.ResourceManager.GetString("Web_SecondFactor_ResetNothingToReset");
        Assert.NotNull(expected);
        Assert.Equal(expected, vm.ErrorMessage);
        Assert.False(vm.ConfirmingReset);
        Assert.False(vm.Status!.IsEnrolled);
        Assert.False(vm.CanReset);
    }

    [Fact]
    public async Task Admin_WithOnlyTheResetPasswordsRight_CannotReset()
    {
        var user = SeedUser("erika");
        ArrangePassword(user);
        var self = Vm(user);
        await self.LoadAsync(user);
        await SetUpAsync(self);

        var admin = SeedUser("admin");
        _mgmt.Setup(m => m.CanManageUserAsync(It.Is<UserContext>(c => c.User.Id == admin.Id), user.Id, ManagementPermission.ResetPasswords))
            .ReturnsAsync(true);
        var vm = Vm(admin);
        await vm.LoadAsync(user);

        Assert.False(vm.CanReset);
        await vm.ResetAsync();
        Assert.NotNull(await new SecondFactorRepository(DbFactory).GetAsync(user.Id));
    }

    [Fact]
    public async Task Admin_WithoutPermission_CannotReset()
    {
        var user = SeedUser("erika");
        ArrangePassword(user);
        var self = Vm(user);
        await self.LoadAsync(user);
        await SetUpAsync(self);

        var admin = SeedUser("admin");
        var vm = Vm(admin);
        await vm.LoadAsync(user);

        Assert.False(vm.CanReset);
        await vm.ResetAsync();
        Assert.NotNull(await new SecondFactorRepository(DbFactory).GetAsync(user.Id));
    }

    [Fact]
    public async Task OwnAccount_IsNeverResetThroughTheAdminAction()
    {
        var user = SeedUser("erika");
        ArrangePassword(user);
        _mgmt.Setup(m => m.CanManageUserAsync(It.IsAny<UserContext>(), It.IsAny<Guid>(), It.IsAny<ManagementPermission>()))
            .ReturnsAsync(true);
        var vm = Vm(user);
        await vm.LoadAsync(user);
        await SetUpAsync(vm);

        Assert.False(vm.CanReset);
        await vm.ResetAsync();
        Assert.NotNull(await new SecondFactorRepository(DbFactory).GetAsync(user.Id));
    }

    [Fact]
    public async Task OtherUsers_CannotStartTheSetUp()
    {
        var user = SeedUser("erika");
        var admin = SeedUser("admin");
        var vm = Vm(admin);
        await vm.LoadAsync(user);

        Assert.False(vm.CanSetUp);
        vm.StartSetup();
        Assert.Equal(SecondFactorPanelMode.None, vm.Mode);
    }
}
