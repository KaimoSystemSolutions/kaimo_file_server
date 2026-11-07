using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.ViewModels.Auth;

/// <summary>
/// The second-factor steps of the web login: no session before the code, the code step for
/// enrolled accounts, forced enrollment inside the login page, the pending-login timeout, and
/// the session token that records the second factor.
/// </summary>
public class LoginViewModelSecondFactorTests : DatabaseTestBase
{
    private const string Password = "correct-horse";

    private readonly MutableTimeProvider _time = new() { Now = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero) };
    private readonly IDataProtectionProvider _dataProtection = new EphemeralDataProtectionProvider();
    private readonly LoginThrottle _throttle;
    private readonly ConfigRepository _config;
    private readonly JwtTokenService _jwt;
    private readonly Mock<IJSRuntime> _js = new();
    private readonly Mock<ILoginService> _login = new();
    private readonly SecurityMonitor _monitor = new();
    private int _sessionsStored;

    public LoginViewModelSecondFactorTests()
    {
        _throttle = new LoginThrottle(_time);
        _config = new ConfigRepository(DbFactory, new MemoryCache(new MemoryCacheOptions()));
        _jwt = new JwtTokenService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = "unit-test-jwt-secret-at-least-32-characters-long!!",
            })
            .Build(), NullLogger<JwtTokenService>.Instance);
        _js.Setup(j => j.InvokeAsync<bool>("kaimoSession.post", It.IsAny<object?[]?>()))
            .Callback(() => _sessionsStored++)
            .Returns(new ValueTask<bool>(true));
    }

    private SecondFactorPolicyService Policy() => new(
        _config, new UserContextFactory(DbFactory), DepartmentRepo(), ScopedRoleRepo(), RoleRepo(),
        new SecondFactorRepository(DbFactory), new MemoryCache(new MemoryCacheOptions()));

    private SecondFactorService SecondFactor() => new(
        new SecondFactorRepository(DbFactory), Policy(), _dataProtection, _throttle, _config,
        Mock.Of<INotificationPublisher>(), new SecurityMonitor(), new SecurityEventRepository(DbFactory),
        NullLogger<SecondFactorService>.Instance, _time);

    private (LoginViewModel Vm, JwtAuthenticationStateProvider Auth) NewVm()
    {
        var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var auth = new JwtAuthenticationStateProvider(_js.Object, _jwt, new WebSessionTicketStore(), scopeFactory,
            NullLogger<JwtAuthenticationStateProvider>.Instance);
        var vm = new LoginViewModel(_login.Object, _jwt, auth, NullLogger<LoginViewModel>.Instance,
            new ClientConnectionInfo { RemoteAddress = "198.51.100.4" }, Policy(), SecondFactor(),
            new UserContextFactory(DbFactory), _time, _monitor);
        return (vm, auth);
    }

    private User ArrangePasswordOk(string username = "erika")
    {
        var user = SeedUser(username);
        _login.Setup(l => l.AuthenticateAsync(username, Password, It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(LoginResult.ForSuccess(new UserContext(user, [], [], [])));
        return user;
    }

    private async Task<byte[]> EnrollAsync(User user)
    {
        var service = SecondFactor();
        var enrollment = await service.BeginEnrollmentAsync(user);
        await service.ConfirmEnrollmentAsync(user, enrollment.Secret, Totp.Compute(enrollment.Secret, Totp.StepAt(_time.Now)), null);
        _time.Advance(TimeSpan.FromSeconds(Totp.StepSeconds));
        return enrollment.Secret;
    }

    private string CurrentCode(byte[] secret) => Totp.Compute(secret, Totp.StepAt(_time.Now));

    private static async Task<bool> SessionHasSecondFactorAsync(JwtAuthenticationStateProvider auth)
        => JwtTokenService.HasSecondFactor((await auth.GetAuthenticationStateAsync()).User);

    [Fact]
    public async Task WithoutSecondFactor_PasswordSignsIn()
    {
        ArrangePasswordOk();
        var (vm, auth) = NewVm();
        vm.Username = "erika";
        vm.Password = Password;

        Assert.True(await vm.LoginAsync());
        Assert.Equal(LoginStage.Credentials, vm.Stage);
        Assert.False(await SessionHasSecondFactorAsync(auth));
    }

    [Fact]
    public async Task EnrolledUser_GetsTheCodeStep_AndNoSessionBeforeIt()
    {
        var user = ArrangePasswordOk();
        var secret = await EnrollAsync(user);
        var (vm, auth) = NewVm();
        vm.Username = "erika";
        vm.Password = Password;

        Assert.False(await vm.LoginAsync());
        Assert.Equal(LoginStage.SecondFactor, vm.Stage);
        Assert.Equal("", vm.Password);
        Assert.Equal(0, _sessionsStored);

        vm.Code = CurrentCode(secret);
        Assert.True(await vm.VerifySecondFactorAsync());
        Assert.Equal(1, _sessionsStored);
        Assert.True(await SessionHasSecondFactorAsync(auth));
    }

    [Fact]
    public async Task WrongCode_StaysOnTheCodeStepWithAnError()
    {
        var user = ArrangePasswordOk();
        await EnrollAsync(user);
        var (vm, _) = NewVm();
        vm.Username = "erika";
        vm.Password = Password;
        await vm.LoginAsync();

        vm.Code = "000000";
        Assert.False(await vm.VerifySecondFactorAsync());
        Assert.Equal(LoginStage.SecondFactor, vm.Stage);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Equal(0, _sessionsStored);
    }

    [Fact]
    public async Task PendingLogin_ExpiresAfterFiveMinutes()
    {
        var user = ArrangePasswordOk();
        var secret = await EnrollAsync(user);
        var (vm, _) = NewVm();
        vm.Username = "erika";
        vm.Password = Password;
        await vm.LoginAsync();

        _time.Advance(TimeSpan.FromMinutes(6));
        vm.Code = CurrentCode(secret);

        Assert.False(await vm.VerifySecondFactorAsync());
        Assert.Equal(LoginStage.Credentials, vm.Stage);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Equal(0, _sessionsStored);
    }

    [Fact]
    public async Task PendingLogin_IsDropped_WhenTheSecurityStampChanged()
    {
        var user = ArrangePasswordOk();
        var secret = await EnrollAsync(user);
        var (vm, _) = NewVm();
        vm.Username = "erika";
        vm.Password = Password;
        await vm.LoginAsync();

        // e.g. an administrator reset the password between the two steps.
        await UserRepo().UpdatePasswordAsync(user.Id, "new-hash", "new-nt");
        vm.Code = CurrentCode(secret);

        Assert.False(await vm.VerifySecondFactorAsync());
        Assert.Equal(LoginStage.Credentials, vm.Stage);
        Assert.Equal(0, _sessionsStored);
    }

    [Fact]
    public async Task RequiredButNotEnrolled_EnrollsInsideTheLogin_ThenShowsRecoveryCodes()
    {
        ArrangePasswordOk();
        await Policy().SavePolicyAsync(new SecondFactorPolicy { Mode = SecondFactorMode.Required, AppliesToAll = true });
        var (vm, auth) = NewVm();
        vm.Username = "erika";
        vm.Password = Password;

        Assert.False(await vm.LoginAsync());
        Assert.Equal(LoginStage.Enrollment, vm.Stage);
        Assert.NotNull(vm.Enrollment);

        vm.Code = CurrentCode(vm.Enrollment!.Secret);
        Assert.False(await vm.ConfirmEnrollmentAsync());
        Assert.Equal(LoginStage.RecoveryCodes, vm.Stage);
        Assert.Equal(SecondFactorService.RecoveryCodeCount, vm.RecoveryCodes!.Count);
        Assert.Null(vm.Enrollment);
        Assert.Equal(0, _sessionsStored);

        Assert.True(await vm.FinishEnrollmentAsync());
        Assert.True(await SessionHasSecondFactorAsync(auth));
    }

    [Fact]
    public async Task ForcedEnrollment_GetsItsOwnLongerTimeLimit()
    {
        ArrangePasswordOk();
        await Policy().SavePolicyAsync(new SecondFactorPolicy { Mode = SecondFactorMode.Required, AppliesToAll = true });
        var (vm, _) = NewVm();
        vm.Username = "erika";
        vm.Password = Password;
        await vm.LoginAsync();

        // Installing the authenticator app easily takes longer than the code step's limit.
        _time.Advance(LoginViewModel.PendingLoginLifetime + TimeSpan.FromMinutes(1));
        vm.Code = CurrentCode(vm.Enrollment!.Secret);

        Assert.False(await vm.ConfirmEnrollmentAsync());
        Assert.Equal(LoginStage.RecoveryCodes, vm.Stage);
    }

    [Fact]
    public async Task ForcedEnrollment_StillExpires()
    {
        var user = ArrangePasswordOk();
        await Policy().SavePolicyAsync(new SecondFactorPolicy { Mode = SecondFactorMode.Required, AppliesToAll = true });
        var (vm, _) = NewVm();
        vm.Username = "erika";
        vm.Password = Password;
        await vm.LoginAsync();

        _time.Advance(LoginViewModel.EnrollmentLifetime + TimeSpan.FromMinutes(1));
        vm.Code = CurrentCode(vm.Enrollment!.Secret);

        Assert.False(await vm.ConfirmEnrollmentAsync());
        Assert.Equal(LoginStage.Credentials, vm.Stage);
        Assert.Null(await new SecondFactorRepository(DbFactory).GetAsync(user.Id));
    }

    [Fact]
    public async Task DoubleSubmit_ChecksTheCodeOnlyOnce()
    {
        var user = ArrangePasswordOk();
        var secret = await EnrollAsync(user);
        var (vm, _) = NewVm();
        vm.Username = "erika";
        vm.Password = Password;
        await vm.LoginAsync();

        // Hold the first submit in the session handoff, after its code was accepted.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _js.Setup(j => j.InvokeAsync<bool>("kaimoSession.post", It.IsAny<object?[]?>()))
            .Callback(() => entered.TrySetResult())
            .Returns(() => new ValueTask<bool>(gate.Task));

        vm.Code = CurrentCode(secret);
        var first = vm.VerifySecondFactorAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        vm.Code = CurrentCode(secret);
        var second = await vm.VerifySecondFactorAsync();
        gate.SetResult(true);

        Assert.True(await first);
        Assert.False(second);
        // The second submit did not count as a wrong code.
        int max = (await _config.LoadLoginProtectionAsync()).MaxAttempts;
        for (int i = 0; i < max - 1; i++)
            Assert.Equal(SecondFactorCheck.Invalid, (await SecondFactor().VerifyAsync(user, "000000", null)).Outcome);
    }

    private async Task<List<LoginOutcome>> LoggedOutcomesAsync()
    {
        var events = new SecurityEventRepository(DbFactory);
        await _monitor.FlushAsync(events);
        return (await events.ListLoginAttemptsAsync(DateTime.UtcNow.AddDays(-1), null, failedOnly: false, 10))
            .Select(a => a.Outcome).ToList();
    }

    [Fact]
    public async Task CodeLogin_LogsTheSuccessOnceTheCodeIsAccepted()
    {
        var user = ArrangePasswordOk();
        var secret = await EnrollAsync(user);
        var (vm, _) = NewVm();
        vm.Username = "erika";
        vm.Password = Password;
        await vm.LoginAsync();
        Assert.Empty(await LoggedOutcomesAsync());

        vm.Code = CurrentCode(secret);
        await vm.VerifySecondFactorAsync();

        Assert.Equal([LoginOutcome.Success], await LoggedOutcomesAsync());
    }

    [Fact]
    public async Task ForcedEnrollment_LogsTheSuccessWhenTheSessionStarts()
    {
        ArrangePasswordOk();
        await Policy().SavePolicyAsync(new SecondFactorPolicy { Mode = SecondFactorMode.Required, AppliesToAll = true });
        var (vm, _) = NewVm();
        vm.Username = "erika";
        vm.Password = Password;
        await vm.LoginAsync();
        vm.Code = CurrentCode(vm.Enrollment!.Secret);
        await vm.ConfirmEnrollmentAsync();
        Assert.Empty(await LoggedOutcomesAsync());

        await vm.FinishEnrollmentAsync();

        Assert.Equal([LoginOutcome.Success], await LoggedOutcomesAsync());
    }

    [Fact]
    public async Task PasswordOnlyLogin_IsLoggedByTheLoginServiceAlone()
    {
        ArrangePasswordOk();
        var (vm, _) = NewVm();
        vm.Username = "erika";
        vm.Password = Password;

        Assert.True(await vm.LoginAsync());

        Assert.Empty(await LoggedOutcomesAsync()); // no second Success next to the login service's
    }

    [Fact]
    public async Task Cancel_ReturnsToTheCredentials()
    {
        var user = ArrangePasswordOk();
        await EnrollAsync(user);
        var (vm, _) = NewVm();
        vm.Username = "erika";
        vm.Password = Password;
        await vm.LoginAsync();

        vm.CancelSecondFactor();

        Assert.Equal(LoginStage.Credentials, vm.Stage);
        Assert.False(await vm.VerifySecondFactorAsync());
        Assert.Equal(0, _sessionsStored);
    }
}
