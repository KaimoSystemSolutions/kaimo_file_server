using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Services;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// The per-client-address budget: guessing with ever-new usernames from one address never
/// reaches a (user, address) or account-wide limit, so the address itself is limited. Once its
/// budget is spent, further attempts are refused before any database lookup or password hash.
/// </summary>
public partial class CredentialLoginServiceTests
{
    private const string Attacker = "203.0.113.66";

    private void ArrangeUnknownUsers()
        => _users.Setup(r => r.GetByUsernameAsync(It.Is<string>(n => n != Username))).ReturnsAsync((User?)null);

    private async Task FailWithDistinctUsernamesAsync(int count, string address, int offset = 0)
    {
        for (int i = 0; i < count; i++)
            await _sut.AuthenticateAsync($"guess{offset + i}", "wrong", address);
    }

    [Fact]
    public async Task DistinctUsernamesFromOneAddress_LockTheAddress_WithoutHashing()
    {
        ArrangeExistingUser();
        ArrangeUnknownUsers();

        await FailWithDistinctUsernamesAsync(IpMaxAttempts, Attacker);
        _passwords.Invocations.Clear();
        _users.Invocations.Clear();

        var next = await _sut.AuthenticateAsync("another-guess", "wrong", Attacker);
        var correct = await _sut.AuthenticateAsync(Username, CorrectPassword, Attacker);

        Assert.Equal(LoginOutcome.LockedOut, next.Outcome);
        Assert.Equal(LoginOutcome.LockedOut, correct.Outcome);
        _passwords.Verify(p => p.VerifyPassword(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _users.Verify(r => r.GetByUsernameAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task TheFailureThatExhaustsTheAddressBudget_ReportsTheAddressLockout()
    {
        ArrangeUnknownUsers();

        await FailWithDistinctUsernamesAsync(IpMaxAttempts - 1, Attacker);
        var last = await _sut.AuthenticateAsync("last-guess", "wrong", Attacker);

        Assert.Equal(LoginOutcome.LockedOut, last.Outcome);
        Assert.Equal(TimeSpan.FromMinutes(IpLockoutMinutes), last.RetryAfter);
    }

    [Fact]
    public async Task AddressLockout_DoesNotAffectOtherAddresses()
    {
        ArrangeExistingUser();
        ArrangeUnknownUsers();

        await FailWithDistinctUsernamesAsync(IpMaxAttempts, Attacker);

        Assert.Equal(LoginOutcome.Success,
            (await _sut.AuthenticateAsync(Username, CorrectPassword, "192.0.2.44")).Outcome);
    }

    [Fact]
    public async Task WithoutKnownAddress_NoAddressBudgetApplies()
    {
        ArrangeUnknownUsers();

        for (int i = 0; i < IpMaxAttempts * 3; i++)
            Assert.Equal(LoginOutcome.InvalidCredentials,
                (await _sut.AuthenticateAsync($"guess{i}", "wrong")).Outcome);
    }

    [Fact]
    public async Task AddressLockout_Expires()
    {
        ArrangeExistingUser();
        ArrangeUnknownUsers();
        await FailWithDistinctUsernamesAsync(IpMaxAttempts, Attacker);

        _time.Advance(TimeSpan.FromMinutes(IpLockoutMinutes - 1));
        Assert.Equal(LoginOutcome.LockedOut,
            (await _sut.AuthenticateAsync(Username, CorrectPassword, Attacker)).Outcome);

        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(LoginOutcome.Success,
            (await _sut.AuthenticateAsync(Username, CorrectPassword, Attacker)).Outcome);
    }

    [Fact]
    public async Task SuccessfulLogin_ResetsTheAddressBudget()
    {
        ArrangeExistingUser();
        ArrangeUnknownUsers();

        await FailWithDistinctUsernamesAsync(IpMaxAttempts - 1, Attacker);
        Assert.Equal(LoginOutcome.Success,
            (await _sut.AuthenticateAsync(Username, CorrectPassword, Attacker)).Outcome);

        // Without the reset these failures would exceed the address budget.
        await FailWithDistinctUsernamesAsync(IpMaxAttempts - 2, Attacker, offset: 100);
        Assert.Equal(LoginOutcome.InvalidCredentials,
            (await _sut.AuthenticateAsync("guess-final", "wrong", Attacker)).Outcome);
    }

    [Fact]
    public async Task DisabledAccount_FromOneAddress_NeverConsumesTheAddressBudget()
    {
        ArrangeExistingUser(enabled: false);

        for (int i = 0; i < IpMaxAttempts * 3; i++)
            Assert.Equal(LoginOutcome.AccountDisabled,
                (await _sut.AuthenticateAsync(Username, CorrectPassword, Attacker)).Outcome);
    }

    [Fact]
    public async Task RepositoryFailure_FromOneAddress_DoesNotConsumeTheAddressBudget()
    {
        var user = MakeUser();
        ArrangeExistingUser();
        int calls = 0;
        _users.Setup(r => r.GetByUsernameAsync(Username))
            .Returns(() => ++calls <= IpMaxAttempts * 2
                ? Task.FromException<User?>(new InvalidOperationException("db down"))
                : Task.FromResult<User?>(user));

        for (int i = 0; i < IpMaxAttempts * 2; i++)
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => _sut.AuthenticateAsync(Username, CorrectPassword, Attacker));

        Assert.Equal(LoginOutcome.Success,
            (await _sut.AuthenticateAsync(Username, CorrectPassword, Attacker)).Outcome);
    }

    [Fact]
    public async Task UserAddressLockout_ReleasesTheAddressReservation()
    {
        ArrangeExistingUser();
        ArrangeUnknownUsers();

        // Lock (alice, address) with MaxAttempts failures; the address has budget left.
        for (int i = 0; i < MaxAttempts; i++)
            await _sut.AuthenticateAsync(Username, "wrong", Attacker);
        // Attempts refused by the (alice, address) lock must not leak address reservations …
        for (int i = 0; i < IpMaxAttempts * 2; i++)
            Assert.Equal(LoginOutcome.LockedOut,
                (await _sut.AuthenticateAsync(Username, CorrectPassword, Attacker)).Outcome);

        // … so the remaining address budget still admits one more real check.
        Assert.Equal(LoginOutcome.LockedOut,
            (await _sut.AuthenticateAsync("guess", "wrong", Attacker)).Outcome); // 4th failure locks the address
        _passwords.Verify(p => p.VerifyPassword("wrong", It.IsAny<string>()), Times.Exactly(IpMaxAttempts));
    }

    [Fact]
    public async Task ParallelDistinctUsernames_VerifyAtMostTheAddressBudget()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _users.Setup(r => r.GetByUsernameAsync(It.IsAny<string>()))
            .Returns(async () => { await gate.Task; return (User?)null; });
        int verifications = 0;
        _passwords.Setup(p => p.VerifyPassword(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(() => { Interlocked.Increment(ref verifications); return false; });

        var attempts = Enumerable.Range(0, 20)
            .Select(i => _sut.AuthenticateAsync($"guess{i}", "wrong", Attacker))
            .ToList();
        gate.SetResult();
        var results = await Task.WhenAll(attempts);

        Assert.True(verifications <= IpMaxAttempts, $"{verifications} hashes verified");
        Assert.True(results.Count(r => r.Outcome == LoginOutcome.LockedOut) >= 20 - IpMaxAttempts);
    }

    [Fact]
    public async Task AccountWideFactor_ComesFromConfiguration()
    {
        _config.Setup(c => c.GetIntAsync("security.login.accountWideFactor", It.IsAny<int>())).ReturnsAsync(2);
        ArrangeExistingUser();

        for (int i = 0; i < MaxAttempts * 2; i++)
            await _sut.AuthenticateAsync(Username, "wrong", $"198.51.100.{i + 1}");

        Assert.Equal(LoginOutcome.LockedOut,
            (await _sut.AuthenticateAsync(Username, CorrectPassword, "192.0.2.200")).Outcome);
    }

    [Fact]
    public void AccountWideAttemptFactor_DefaultsToTen()
        => Assert.Equal(LoginProtectionSettings.DefaultAccountWideFactor, CredentialLoginService.AccountWideAttemptFactor);

    [Theory]
    [InlineData("10.0.0.1", "|ip|10.0.0.1")]
    [InlineData(" 10.0.0.1 ", "|ip|10.0.0.1")]
    [InlineData("::ffff:10.0.0.1", "|ip|10.0.0.1")]
    [InlineData("2001:DB8::1", "|ip|2001:db8::1")]
    [InlineData(null, "")]
    [InlineData(" ", "")]
    public void AddressThrottleKey_IsCanonical_AndEmptyWithoutAddress(string? address, string expected)
        => Assert.Equal(expected, CredentialLoginService.AddressThrottleKey(address));
}
