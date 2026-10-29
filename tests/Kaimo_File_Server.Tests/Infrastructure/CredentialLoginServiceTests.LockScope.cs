using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Services;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Which budget a LockedOut result comes from. A locked client address refuses every user
/// name, so its lockout must not be mistaken for a locked account (notifications, overview):
/// only a (user, address) or account-wide lock is reported with <see cref="LockoutScope.Account"/>.
/// </summary>
public partial class CredentialLoginServiceTests
{
    private const string Office = "192.0.2.10";

    [Fact]
    public async Task AddressLockout_IsReportedWithTheAddressScope()
    {
        ArrangeExistingUser();
        ArrangeUnknownUsers();

        await FailWithDistinctUsernamesAsync(IpMaxAttempts - 1, Attacker);
        var exhausting = await _sut.AuthenticateAsync("last-guess", "wrong", Attacker);
        var later = await _sut.AuthenticateAsync(Username, CorrectPassword, Attacker);

        Assert.Equal(LoginOutcome.LockedOut, exhausting.Outcome);
        Assert.Equal(LockoutScope.Address, exhausting.LockScope);
        Assert.Equal(LoginOutcome.LockedOut, later.Outcome);
        Assert.Equal(LockoutScope.Address, later.LockScope);
    }

    [Fact]
    public async Task UserAddressLockout_IsReportedWithTheAccountScope()
    {
        ArrangeExistingUser();

        LoginResult last = null!;
        for (int i = 0; i < MaxAttempts; i++)
            last = await _sut.AuthenticateAsync(Username, "wrong", Office);
        var later = await _sut.AuthenticateAsync(Username, CorrectPassword, Office);

        Assert.Equal(LoginOutcome.LockedOut, last.Outcome);
        Assert.Equal(LockoutScope.Account, last.LockScope);
        Assert.Equal(LoginOutcome.LockedOut, later.Outcome);
        Assert.Equal(LockoutScope.Account, later.LockScope);
    }

    [Fact]
    public async Task AccountWideLockout_IsReportedWithTheAccountScope()
    {
        ArrangeExistingUser();

        for (int i = 0; i < MaxAttempts * CredentialLoginService.AccountWideAttemptFactor; i++)
            await _sut.AuthenticateAsync(Username, "wrong", $"198.51.{i / 200}.{i % 200 + 1}");
        var fresh = await _sut.AuthenticateAsync(Username, CorrectPassword, "192.0.2.200");

        Assert.Equal(LoginOutcome.LockedOut, fresh.Outcome);
        Assert.Equal(LockoutScope.Account, fresh.LockScope);
    }

    [Fact]
    public async Task FailureThatLocksAddressAndAccount_ReportsTheAccountScope_WithTheLongerLock()
    {
        ArrangeExistingUser();
        ArrangeUnknownUsers();

        // One guess plus MaxAttempts failures for alice: the last one exhausts both budgets.
        await FailWithDistinctUsernamesAsync(IpMaxAttempts - MaxAttempts, Attacker);
        LoginResult last = null!;
        for (int i = 0; i < MaxAttempts; i++)
            last = await _sut.AuthenticateAsync(Username, "wrong", Attacker);

        Assert.Equal(LoginOutcome.LockedOut, last.Outcome);
        Assert.Equal(LockoutScope.Account, last.LockScope);
        Assert.Equal(TimeSpan.FromMinutes(IpLockoutMinutes), last.RetryAfter);
    }

    [Fact]
    public void LockedOut_WithoutScope_IsAnAccountLockout()
        => Assert.Equal(LockoutScope.Account, LoginResult.LockedOut(TimeSpan.FromMinutes(1)).LockScope);
}
