using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Infrastructure.Services;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Regression tests for credential authentication: brute-force lockout and
/// username-enumeration resistance. Uses a real <see cref="LoginThrottle"/>
/// (driven by a controllable clock) plus mocked repositories.
/// </summary>
public class CredentialLoginServiceTests
{
    private const string Username = "alice";
    private const string CorrectPassword = "correct-horse";
    private const string UserHash = "hash-for-alice";
    private const int MaxAttempts = 3;
    private const int LockoutMinutes = 10;

    private readonly Mock<IUserRepository> _users = new();
    private readonly Mock<IPasswordService> _passwords = new();
    private readonly Mock<IUserContextFactory> _contextFactory = new();
    private readonly Mock<IConfigRepository> _config = new();
    private readonly MutableTimeProvider _time = new();
    private readonly CredentialLoginService _sut;

    public CredentialLoginServiceTests()
    {
        _config.Setup(c => c.GetIntAsync("app.user.maxLoginAttempts", It.IsAny<int>()))
            .ReturnsAsync(MaxAttempts);
        _config.Setup(c => c.GetIntAsync("app.user.lockoutMinutes", It.IsAny<int>()))
            .ReturnsAsync(LockoutMinutes);

        var throttle = new LoginThrottle(_time);
        _sut = new CredentialLoginService(
            _users.Object, _passwords.Object, _contextFactory.Object, throttle, _config.Object);
    }

    private static User MakeUser(bool enabled = true) =>
        new(Guid.NewGuid(), "Alice", Username, UserHash, "0123456789ABCDEF0123456789ABCDEF",
            isEnabled: enabled);

    private void ArrangeExistingUser(bool enabled = true)
    {
        var user = MakeUser(enabled);
        _users.Setup(r => r.GetByUsernameAsync(Username)).ReturnsAsync(user);
        _passwords.Setup(p => p.VerifyPassword(CorrectPassword, UserHash)).Returns(true);
        _contextFactory.Setup(f => f.CreateAsync(It.IsAny<User>()))
            .ReturnsAsync((User u) => new UserContext(
                u, new HashSet<Group>(), new HashSet<Role>(), new HashSet<string>()));
    }

    // ─────────────── Enumeration resistance ───────────────

    [Fact]
    public async Task UnknownUser_ReturnsInvalidCredentials_AndStillVerifiesAHash()
    {
        _users.Setup(r => r.GetByUsernameAsync(It.IsAny<string>())).ReturnsAsync((User?)null);

        var result = await _sut.AuthenticateAsync("ghost", "whatever");

        Assert.Equal(LoginOutcome.InvalidCredentials, result.Outcome);
        // The dummy verify MUST run so timing doesn't reveal the user is unknown.
        _passwords.Verify(p => p.VerifyPassword(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task UnknownUserAndWrongPassword_AreIndistinguishable()
    {
        _users.Setup(r => r.GetByUsernameAsync("ghost")).ReturnsAsync((User?)null);
        ArrangeExistingUser();

        var unknown = await _sut.AuthenticateAsync("ghost", "x");
        var wrongPw = await _sut.AuthenticateAsync(Username, "wrong");

        Assert.Equal(LoginOutcome.InvalidCredentials, unknown.Outcome);
        Assert.Equal(LoginOutcome.InvalidCredentials, wrongPw.Outcome);
    }

    [Fact]
    public async Task EmptyPassword_IsRejected_WithoutProbingTheRepository()
    {
        ArrangeExistingUser();

        var result = await _sut.AuthenticateAsync(Username, "");

        // No login without a password — even for an otherwise valid, enabled user.
        Assert.Equal(LoginOutcome.InvalidCredentials, result.Outcome);
        // Rejected before any user/hash lookup.
        _users.Verify(r => r.GetByUsernameAsync(It.IsAny<string>()), Times.Never);
        _passwords.Verify(p => p.VerifyPassword(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    // ─────────────── Happy path ───────────────

    [Fact]
    public async Task CorrectPassword_EnabledUser_ReturnsSuccessWithContext()
    {
        ArrangeExistingUser();

        var result = await _sut.AuthenticateAsync(Username, CorrectPassword);

        Assert.Equal(LoginOutcome.Success, result.Outcome);
        Assert.NotNull(result.UserContext);
        Assert.Equal(Username, result.UserContext!.User.Username);
    }

    [Fact]
    public async Task CorrectPassword_DisabledUser_ReturnsAccountDisabled()
    {
        ArrangeExistingUser(enabled: false);

        var result = await _sut.AuthenticateAsync(Username, CorrectPassword);

        Assert.Equal(LoginOutcome.AccountDisabled, result.Outcome);
    }

    // ─────────────── Brute-force lockout ───────────────

    [Fact]
    public async Task RepeatedWrongPassword_LocksOutAtThreshold()
    {
        ArrangeExistingUser();

        Assert.Equal(LoginOutcome.InvalidCredentials,
            (await _sut.AuthenticateAsync(Username, "wrong")).Outcome);
        Assert.Equal(LoginOutcome.InvalidCredentials,
            (await _sut.AuthenticateAsync(Username, "wrong")).Outcome);

        var third = await _sut.AuthenticateAsync(Username, "wrong");
        Assert.Equal(LoginOutcome.LockedOut, third.Outcome);
        Assert.True(third.RetryAfter > TimeSpan.Zero);
    }

    [Fact]
    public async Task WhenLockedOut_CorrectPasswordStillRejected_AndRepositoryNotProbed()
    {
        ArrangeExistingUser();

        for (int i = 0; i < MaxAttempts; i++)
            await _sut.AuthenticateAsync(Username, "wrong");

        // Even the *correct* password is refused while locked …
        var locked = await _sut.AuthenticateAsync(Username, CorrectPassword);
        Assert.Equal(LoginOutcome.LockedOut, locked.Outcome);

        // … and no extra DB lookup happens while locked (no enumeration oracle).
        _users.Verify(r => r.GetByUsernameAsync(Username), Times.Exactly(MaxAttempts));
    }

    [Fact]
    public async Task SuccessfulLogin_ResetsFailureCounter()
    {
        ArrangeExistingUser();

        // Two failures (below the threshold of 3) …
        await _sut.AuthenticateAsync(Username, "wrong");
        await _sut.AuthenticateAsync(Username, "wrong");

        // … then a success must clear the slate.
        Assert.Equal(LoginOutcome.Success,
            (await _sut.AuthenticateAsync(Username, CorrectPassword)).Outcome);

        // Two more failures should therefore NOT lock out yet.
        await _sut.AuthenticateAsync(Username, "wrong");
        var second = await _sut.AuthenticateAsync(Username, "wrong");
        Assert.Equal(LoginOutcome.InvalidCredentials, second.Outcome);
    }

    [Fact]
    public async Task AfterLockoutExpires_LoginSucceedsAgain()
    {
        ArrangeExistingUser();

        for (int i = 0; i < MaxAttempts; i++)
            await _sut.AuthenticateAsync(Username, "wrong");
        Assert.Equal(LoginOutcome.LockedOut,
            (await _sut.AuthenticateAsync(Username, CorrectPassword)).Outcome);

        _time.Advance(TimeSpan.FromMinutes(LockoutMinutes));

        var result = await _sut.AuthenticateAsync(Username, CorrectPassword);
        Assert.Equal(LoginOutcome.Success, result.Outcome);
    }

    // ─────────────── Parallel attempts ───────────────

    [Fact]
    public async Task ParallelWrongPasswords_VerifyAtMostMaxAttemptsHashes()
    {
        ArrangeExistingUser();
        // Hold every request inside the user lookup, i.e. after the lockout
        // gate, so all of them race the gate before the first failure lands.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var user = MakeUser();
        _users.Setup(r => r.GetByUsernameAsync(Username))
            .Returns(async () => { await gate.Task; return user; });
        int verifications = 0;
        _passwords.Setup(p => p.VerifyPassword("wrong", UserHash))
            .Returns(() => { Interlocked.Increment(ref verifications); return false; });

        var attempts = Enumerable.Range(0, 20)
            .Select(_ => _sut.AuthenticateAsync(Username, "wrong", "198.51.100.7"))
            .ToList();
        gate.SetResult();
        var results = await Task.WhenAll(attempts);

        Assert.True(verifications <= MaxAttempts, $"{verifications} hashes verified");
        Assert.True(results.Count(r => r.Outcome == LoginOutcome.LockedOut) >= 20 - MaxAttempts);
    }

    // ─────────────── Lockout per (user, client address) ───────────────

    [Fact]
    public async Task FailuresFromOneAddress_DoNotLockOutOtherAddresses()
    {
        ArrangeExistingUser();

        for (int i = 0; i < MaxAttempts; i++)
            await _sut.AuthenticateAsync(Username, "wrong", "203.0.113.9");
        Assert.Equal(LoginOutcome.LockedOut,
            (await _sut.AuthenticateAsync(Username, CorrectPassword, "203.0.113.9")).Outcome);

        // The real user on another address is not affected by the attacker.
        Assert.Equal(LoginOutcome.Success,
            (await _sut.AuthenticateAsync(Username, CorrectPassword, "192.0.2.44")).Outcome);
    }

    /// <summary>
    /// Guessing spread over many client addresses (distributed, or with forged
    /// X-Forwarded-For values) hits the account-wide ceiling: once reached, even the
    /// correct password from a fresh address is refused until the lockout expires.
    /// </summary>
    [Fact]
    public async Task FailuresSpreadOverManyAddresses_HitAccountWideCeiling()
    {
        ArrangeExistingUser();
        int ceiling = MaxAttempts * CredentialLoginService.AccountWideAttemptFactor;

        for (int i = 0; i < ceiling; i++)
        {
            var result = await _sut.AuthenticateAsync(Username, "wrong", $"198.51.100.{i + 1}");
            Assert.NotEqual(LoginOutcome.Success, result.Outcome);
        }

        Assert.Equal(LoginOutcome.LockedOut,
            (await _sut.AuthenticateAsync(Username, CorrectPassword, "192.0.2.200")).Outcome);

        _time.Advance(TimeSpan.FromMinutes(LockoutMinutes + 1));
        Assert.Equal(LoginOutcome.Success,
            (await _sut.AuthenticateAsync(Username, CorrectPassword, "192.0.2.200")).Outcome);
    }

    [Fact]
    public void AccountThrottleKey_CannotCollideWithAddressKey()
    {
        Assert.Equal("|account|alice", CredentialLoginService.AccountThrottleKey("Alice"));
        Assert.StartsWith("|", CredentialLoginService.AccountThrottleKey("account"));
    }

    [Fact]
    public void ThrottleKey_IsCaseInsensitiveUserPlusAddress()
    {
        Assert.Equal("alice|10.0.0.1", CredentialLoginService.ThrottleKey("Alice", "10.0.0.1"));
        Assert.Equal("alice", CredentialLoginService.ThrottleKey("ALICE", null));
        Assert.Equal("alice", CredentialLoginService.ThrottleKey("alice", " "));
    }

    // ─────────────── Reservations never leak ───────────────

    [Fact]
    public async Task DisabledAccount_WithCorrectPassword_NeverLocksOut()
    {
        ArrangeExistingUser(enabled: false);

        for (int i = 0; i < MaxAttempts * 3; i++)
            Assert.Equal(LoginOutcome.AccountDisabled,
                (await _sut.AuthenticateAsync(Username, CorrectPassword)).Outcome);
    }

    [Fact]
    public async Task RepositoryFailure_DoesNotConsumeLockoutBudget()
    {
        ArrangeExistingUser();
        var user = MakeUser();
        int calls = 0;
        _users.Setup(r => r.GetByUsernameAsync(Username))
            .Returns(() => ++calls <= MaxAttempts * 2
                ? Task.FromException<User?>(new InvalidOperationException("db down"))
                : Task.FromResult<User?>(user));

        for (int i = 0; i < MaxAttempts * 2; i++)
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => _sut.AuthenticateAsync(Username, CorrectPassword));

        Assert.Equal(LoginOutcome.Success,
            (await _sut.AuthenticateAsync(Username, CorrectPassword)).Outcome);
    }
}
