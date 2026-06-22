using System.Collections.Concurrent;

namespace Kaimo_File_Server.Core.Security;

/// <summary>
/// Lockout policy: after <see cref="MaxAttempts"/> consecutive failures a key
/// is locked for <see cref="LockoutDuration"/>.
/// </summary>
public readonly record struct LoginThrottlePolicy(int MaxAttempts, TimeSpan LockoutDuration)
{
    public static LoginThrottlePolicy Default { get; } = new(5, TimeSpan.FromMinutes(15));
}

/// <summary>Result of a throttle check: whether the key is currently locked and for how long.</summary>
public readonly record struct LockoutStatus(bool IsLockedOut, TimeSpan RetryAfter)
{
    public static readonly LockoutStatus NotLocked = new(false, TimeSpan.Zero);
}

/// <summary>
/// Tracks consecutive failed authentication attempts per key (e.g. username)
/// and enforces a temporary lockout. Pure in-memory bookkeeping — no I/O — so
/// it is trivially unit-testable with a fake <see cref="TimeProvider"/>.
/// </summary>
public interface ILoginThrottle
{
    /// <summary>
    /// Is the key currently locked out? Read-only from the caller's point of
    /// view, though an already-expired lockout is cleared lazily as a side effect.
    /// </summary>
    LockoutStatus Check(string key);

    /// <summary>
    /// Records a failed attempt. Returns a locked status once the policy
    /// threshold is reached. While already locked, repeated failures do not
    /// extend the window.
    /// </summary>
    LockoutStatus RegisterFailure(string key, LoginThrottlePolicy policy);

    /// <summary>Clears all state for a key (call on a successful login).</summary>
    void Reset(string key);
}

/// <summary>
/// Thread-safe, in-memory <see cref="ILoginThrottle"/>. Registered as a
/// singleton so counters survive across scoped login requests within a process.
///
/// NOTE: state is per-process. The Web (Blazor Server) login is fully covered;
/// the SMB host runs in a separate process with its own counters. Cross-process
/// lockout would require persisting state — out of scope here.
/// </summary>
public sealed class LoginThrottle : ILoginThrottle
{
    private sealed class State
    {
        public int ConsecutiveFailures;
        public DateTimeOffset? LockedUntil;
    }

    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, State> _states =
        new(StringComparer.OrdinalIgnoreCase);

    public LoginThrottle(TimeProvider? timeProvider = null)
        => _time = timeProvider ?? TimeProvider.System;

    public LockoutStatus Check(string key)
    {
        if (string.IsNullOrEmpty(key) || !_states.TryGetValue(key, out var state))
            return LockoutStatus.NotLocked;

        lock (state)
            return Evaluate(state);
    }

    public LockoutStatus RegisterFailure(string key, LoginThrottlePolicy policy)
    {
        if (string.IsNullOrEmpty(key))
            return LockoutStatus.NotLocked;

        var state = _states.GetOrAdd(key, static _ => new State());

        lock (state)
        {
            var now = _time.GetUtcNow();

            // A previously expired lockout resets the slate.
            if (state.LockedUntil is { } until && now >= until)
            {
                state.LockedUntil = null;
                state.ConsecutiveFailures = 0;
            }

            // Still locked → don't extend, just report remaining time.
            if (state.LockedUntil is { } active && now < active)
                return new LockoutStatus(true, active - now);

            state.ConsecutiveFailures++;

            if (policy.MaxAttempts > 0 && state.ConsecutiveFailures >= policy.MaxAttempts)
            {
                state.LockedUntil = now + policy.LockoutDuration;
                state.ConsecutiveFailures = 0;
                return new LockoutStatus(true, policy.LockoutDuration);
            }

            return LockoutStatus.NotLocked;
        }
    }

    public void Reset(string key)
    {
        if (!string.IsNullOrEmpty(key))
            _states.TryRemove(key, out _);
    }

    private LockoutStatus Evaluate(State state)
    {
        if (state.LockedUntil is { } until)
        {
            var now = _time.GetUtcNow();
            if (now < until)
                return new LockoutStatus(true, until - now);

            // Lockout elapsed — clear so the next attempt starts fresh.
            state.LockedUntil = null;
            state.ConsecutiveFailures = 0;
        }

        return LockoutStatus.NotLocked;
    }
}
