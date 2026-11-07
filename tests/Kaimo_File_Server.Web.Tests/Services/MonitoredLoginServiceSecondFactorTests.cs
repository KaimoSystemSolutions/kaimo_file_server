using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Infrastructure.Repositories;
using Kaimo_File_Server.Infrastructure.Services;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.Services;

/// <summary>
/// The security log of a web login with a second factor: the correct password alone is logged
/// as <see cref="LoginOutcome.PasswordAccepted"/>, which counts neither as a success nor as a
/// failure; other transports and accounts without a second factor still log a success.
/// </summary>
public class MonitoredLoginServiceSecondFactorTests : DatabaseTestBase
{
    private static readonly DateTime Now = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly Mock<ILoginService> _inner = new();
    private readonly SecurityMonitor _monitor = new();

    private SecurityEventRepository Events() => new(DbFactory);

    private SecondFactorPolicyService Policy() => new(
        new ConfigRepository(DbFactory, new MemoryCache(new MemoryCacheOptions())), new UserContextFactory(DbFactory),
        DepartmentRepo(), ScopedRoleRepo(), RoleRepo(), new SecondFactorRepository(DbFactory),
        new MemoryCache(new MemoryCacheOptions()));

    private MonitoredLoginService Sut(string? path = null, SecondFactorPolicyService? policy = null)
    {
        var http = new HttpContextAccessor();
        if (path is not null)
            http.HttpContext = new DefaultHttpContext { Request = { Path = path } };
        return new MonitoredLoginService(_inner.Object, _monitor, http, secondFactor: policy ?? Policy());
    }

    private User ArrangeSuccess(string username)
    {
        var user = SeedUser(username);
        _inner.Setup(s => s.AuthenticateAsync(username, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(LoginResult.ForSuccess(new UserContext(user, [], [], [])));
        return user;
    }

    private Task EnrollAsync(User user)
        => new SecondFactorRepository(DbFactory).ConfirmAsync(user.Id, "protected", 1, [], Now);

    private async Task<LoginOutcome> RecordedOutcomeAsync()
    {
        await _monitor.FlushAsync(Events());
        var attempts = await Events().ListLoginAttemptsAsync(DateTime.UtcNow.AddDays(-1), null, failedOnly: false, 10);
        return Assert.Single(attempts).Outcome;
    }

    [Fact]
    public async Task WebLogin_OfAnAccountWithSecondFactor_LogsOnlyThePassword()
    {
        await EnrollAsync(ArrangeSuccess("erika"));

        var result = await Sut().AuthenticateAsync("erika", "pw", "198.51.100.1");

        Assert.Equal(LoginOutcome.Success, result.Outcome); // the caller still sees the correct password
        Assert.Equal(LoginOutcome.PasswordAccepted, await RecordedOutcomeAsync());
    }

    [Fact]
    public async Task WebLogin_WithoutSecondFactor_LogsSuccess()
    {
        ArrangeSuccess("erika");

        await Sut().AuthenticateAsync("erika", "pw", "198.51.100.1");

        Assert.Equal(LoginOutcome.Success, await RecordedOutcomeAsync());
    }

    [Fact]
    public async Task WebLogin_LogsTheLoginStepTheLoginPageSees_NotACachedSessionResult()
    {
        // The login page decides from the current state; the log must agree with it, or a
        // sign-in is logged twice or never.
        var user = ArrangeSuccess("erika");
        await EnrollAsync(user);
        var policy = Policy();
        Assert.True(await policy.SessionRequiresSecondFactorAsync(user.Id)); // cached "needs a second factor"
        await new SecondFactorRepository(DbFactory).DeleteAsync(user.Id);    // gone without invalidating

        await Sut(policy: policy).AuthenticateAsync("erika", "pw", "198.51.100.1");

        Assert.Equal(LoginOutcome.Success, await RecordedOutcomeAsync());
    }

    [Fact]
    public async Task ApiLogin_OfAnAccountWithSecondFactor_LogsSuccess()
    {
        await EnrollAsync(ArrangeSuccess("erika"));

        await Sut("/api/v1/auth/login").AuthenticateAsync("erika", "pw", "198.51.100.1");

        Assert.Equal(LoginOutcome.Success, await RecordedOutcomeAsync());
    }

    [Fact]
    public async Task PasswordAccepted_IsNeitherSuccessNorFailure()
    {
        _monitor.RecordLogin(SecurityChannel.Web, "erika", "198.51.100.1", new LoginResult(LoginOutcome.PasswordAccepted));
        await _monitor.FlushAsync(Events());

        var since = DateTime.UtcNow.AddDays(-1);
        var totals = await Events().GetTotalsAsync(since, DateTime.UtcNow);
        Assert.Equal(0, totals.Successes);
        Assert.Equal(0, totals.Failures);
        Assert.Empty(await Events().ListLoginAttemptsAsync(since, null, failedOnly: true, 10));
        var account = Assert.Single(await Events().SummarizeAccountsAsync(since, DateTime.UtcNow, 10));
        Assert.Equal(0, account.Failures);
    }
}
