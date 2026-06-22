using Kaimo_File_Server.Core.Security;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// A <see cref="TimeProvider"/> whose clock can be set/advanced by the test,
/// so lockout-expiry behaviour is deterministic without real waiting.
/// </summary>
internal sealed class MutableTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

public class LoginThrottleTests
{
    private static readonly LoginThrottlePolicy Policy = new(3, TimeSpan.FromMinutes(10));

    private readonly MutableTimeProvider _time = new();
    private readonly LoginThrottle _sut;

    public LoginThrottleTests() => _sut = new LoginThrottle(_time);

    [Fact]
    public void FreshKey_IsNotLocked()
        => Assert.False(_sut.Check("alice").IsLockedOut);

    [Fact]
    public void FailuresBelowThreshold_DoNotLock()
    {
        Assert.False(_sut.RegisterFailure("alice", Policy).IsLockedOut);
        Assert.False(_sut.RegisterFailure("alice", Policy).IsLockedOut);
        Assert.False(_sut.Check("alice").IsLockedOut);
    }

    [Fact]
    public void ReachingThreshold_Locks()
    {
        _sut.RegisterFailure("alice", Policy);
        _sut.RegisterFailure("alice", Policy);
        var third = _sut.RegisterFailure("alice", Policy);

        Assert.True(third.IsLockedOut);
        Assert.Equal(TimeSpan.FromMinutes(10), third.RetryAfter);
        Assert.True(_sut.Check("alice").IsLockedOut);
    }

    [Fact]
    public void WhileLocked_FurtherFailuresDoNotExtendWindow()
    {
        for (int i = 0; i < 3; i++) _sut.RegisterFailure("alice", Policy);

        _time.Advance(TimeSpan.FromMinutes(4));
        var status = _sut.RegisterFailure("alice", Policy);

        Assert.True(status.IsLockedOut);
        // 10 min lockout minus the 4 already elapsed → ~6 remaining, NOT reset to 10.
        Assert.Equal(TimeSpan.FromMinutes(6), status.RetryAfter);
    }

    [Fact]
    public void RetryAfter_ShrinksAsTimePasses()
    {
        for (int i = 0; i < 3; i++) _sut.RegisterFailure("alice", Policy);

        _time.Advance(TimeSpan.FromMinutes(7));
        Assert.Equal(TimeSpan.FromMinutes(3), _sut.Check("alice").RetryAfter);
    }

    [Fact]
    public void AfterLockoutExpires_KeyIsUsableAgain()
    {
        for (int i = 0; i < 3; i++) _sut.RegisterFailure("alice", Policy);
        Assert.True(_sut.Check("alice").IsLockedOut);

        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.False(_sut.Check("alice").IsLockedOut);

        // And the counter restarts — a single failure must not immediately re-lock.
        Assert.False(_sut.RegisterFailure("alice", Policy).IsLockedOut);
    }

    [Fact]
    public void Reset_ClearsAccumulatedFailures()
    {
        _sut.RegisterFailure("alice", Policy);
        _sut.RegisterFailure("alice", Policy);
        _sut.Reset("alice");

        // Back to zero: two more failures still shouldn't lock (threshold is 3).
        Assert.False(_sut.RegisterFailure("alice", Policy).IsLockedOut);
        Assert.False(_sut.RegisterFailure("alice", Policy).IsLockedOut);
    }

    [Fact]
    public void KeysAreIndependent()
    {
        for (int i = 0; i < 3; i++) _sut.RegisterFailure("alice", Policy);

        Assert.True(_sut.Check("alice").IsLockedOut);
        Assert.False(_sut.Check("bob").IsLockedOut);
    }

    [Fact]
    public void KeyMatchingIsCaseInsensitive()
    {
        for (int i = 0; i < 3; i++) _sut.RegisterFailure("Alice", Policy);
        Assert.True(_sut.Check("alice").IsLockedOut);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NullOrEmptyKey_IsIgnoredSafely(string? key)
    {
        Assert.False(_sut.RegisterFailure(key!, Policy).IsLockedOut);
        Assert.False(_sut.Check(key!).IsLockedOut);
        _sut.Reset(key!); // must not throw
    }
}
