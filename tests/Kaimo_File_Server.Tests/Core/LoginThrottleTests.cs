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

    // ─────────────── Reservations (parallel attempts) ───────────────

    [Fact]
    public void BeginAttempt_OutstandingReservationsCannotExceedBudget()
    {
        // Three requests are inside the slow hash verify at the same time …
        for (int i = 0; i < 3; i++)
            Assert.False(_sut.BeginAttempt("alice", Policy).IsLockedOut);

        // … so a fourth must not get past the gate, and the key is now locked.
        var fourth = _sut.BeginAttempt("alice", Policy);
        Assert.True(fourth.IsLockedOut);
        Assert.True(_sut.Check("alice").IsLockedOut);
    }

    [Fact]
    public void RegisterFailure_ConsumesReservationInsteadOfCountingTwice()
    {
        _sut.BeginAttempt("alice", Policy);
        Assert.False(_sut.RegisterFailure("alice", Policy).IsLockedOut);
        _sut.BeginAttempt("alice", Policy);
        Assert.False(_sut.RegisterFailure("alice", Policy).IsLockedOut);

        Assert.False(_sut.BeginAttempt("alice", Policy).IsLockedOut);
        Assert.True(_sut.RegisterFailure("alice", Policy).IsLockedOut);
    }

    [Fact]
    public void EndAttempt_ReleasesReservationWithoutCounting()
    {
        for (int i = 0; i < 10; i++)
        {
            Assert.False(_sut.BeginAttempt("alice", Policy).IsLockedOut);
            _sut.EndAttempt("alice");
        }
    }

    [Fact]
    public void Reset_AfterReservation_ClearsState()
    {
        _sut.RegisterFailure("alice", Policy);
        _sut.RegisterFailure("alice", Policy);
        _sut.BeginAttempt("alice", Policy);
        _sut.Reset("alice");

        Assert.False(_sut.BeginAttempt("alice", Policy).IsLockedOut);
        Assert.False(_sut.RegisterFailure("alice", Policy).IsLockedOut);
    }

    [Fact]
    public void BeginAttempt_ParallelCallers_OnlyBudgetPasses()
    {
        int passed = 0;
        Parallel.For(0, 200, _ =>
        {
            if (!_sut.BeginAttempt("alice", Policy).IsLockedOut)
                Interlocked.Increment(ref passed);
        });
        Assert.Equal(Policy.MaxAttempts, passed);
    }

    // ─────────────── Decay and memory bound ───────────────

    [Fact]
    public void FailuresOlderThanWindow_Decay()
    {
        _sut.RegisterFailure("alice", Policy);
        _sut.RegisterFailure("alice", Policy);

        _time.Advance(TimeSpan.FromMinutes(10));

        // Two stale failures + one fresh one must not reach the threshold of 3.
        Assert.False(_sut.RegisterFailure("alice", Policy).IsLockedOut);
        Assert.False(_sut.RegisterFailure("alice", Policy).IsLockedOut);
    }

    [Fact]
    public void ManyIdleKeys_AreSweptOnceExpired()
    {
        for (int i = 0; i < LoginThrottle.SweepThreshold; i++)
            _sut.RegisterFailure($"guess-{i}", Policy);
        Assert.Equal(LoginThrottle.SweepThreshold, _sut.TrackedKeyCount);

        _time.Advance(TimeSpan.FromMinutes(11));
        _sut.RegisterFailure("trigger", Policy);

        Assert.True(_sut.TrackedKeyCount < 10, $"{_sut.TrackedKeyCount} keys left");
    }

    [Fact]
    public void Sweep_KeepsLockedAndReservedKeys()
    {
        var longLock = new LoginThrottlePolicy(1, TimeSpan.FromHours(1));
        _sut.RegisterFailure("locked", longLock);
        _sut.BeginAttempt("reserved", Policy);
        for (int i = 0; i < LoginThrottle.SweepThreshold; i++)
            _sut.RegisterFailure($"guess-{i}", Policy);

        _time.Advance(TimeSpan.FromMinutes(11));
        _sut.RegisterFailure("trigger", Policy);

        Assert.True(_sut.Check("locked").IsLockedOut);
        // The reservation survived: two more reserve the rest of the budget.
        _sut.BeginAttempt("reserved", Policy);
        _sut.BeginAttempt("reserved", Policy);
        Assert.True(_sut.BeginAttempt("reserved", Policy).IsLockedOut);
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
