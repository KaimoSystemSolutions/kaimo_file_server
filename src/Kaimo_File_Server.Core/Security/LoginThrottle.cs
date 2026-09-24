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
    /// Atomically checks the lockout and reserves one attempt before the
    /// (slow) credential verification runs, so concurrent requests cannot all
    /// pass the check. Settle every non-locked reservation with exactly one of
    /// <see cref="RegisterFailure"/>, <see cref="Reset"/> or
    /// <see cref="EndAttempt"/>. When the reserved and failed attempts already
    /// exhaust the policy, the key is locked immediately.
    /// </summary>
    LockoutStatus BeginAttempt(string key, LoginThrottlePolicy policy);

    /// <summary>Releases a reservation from <see cref="BeginAttempt"/> without counting it.</summary>
    void EndAttempt(string key);

    /// <summary>
    /// Records a failed attempt. Returns a locked status once the policy
    /// threshold is reached. While already locked, repeated failures do not
    /// extend the window. Consumes a reservation from <see cref="BeginAttempt"/>
    /// when one is outstanding. Failures older than the lockout duration decay.
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
        public int InFlight;
        public DateTimeOffset LastFailureAt;
        public DateTimeOffset? LockedUntil;
    }

    /// <summary>Above this many tracked keys, idle entries are swept.</summary>
    public const int SweepThreshold = 10_000;
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, State> _states =
        new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastSweep = DateTimeOffset.MinValue;
    // Longest lockout window seen; a failure younger than it may still count.
    private TimeSpan _longestWindow = TimeSpan.Zero;

    /// <summary>Number of keys currently tracked (diagnostics and tests).</summary>
    public int TrackedKeyCount => _states.Count;

    public LoginThrottle(TimeProvider? timeProvider = null)
        => _time = timeProvider ?? TimeProvider.System;

    public LockoutStatus Check(string key)
    {
        if (string.IsNullOrEmpty(key) || !_states.TryGetValue(key, out var state))
            return LockoutStatus.NotLocked;

        lock (state)
            return Evaluate(state);
    }

    public LockoutStatus BeginAttempt(string key, LoginThrottlePolicy policy)
    {
        if (string.IsNullOrEmpty(key))
            return LockoutStatus.NotLocked;

        SweepIfNeeded(policy);
        var state = _states.GetOrAdd(key, static _ => new State());

        lock (state)
        {
            var now = _time.GetUtcNow();
            Refresh(state, now, policy);

            if (state.LockedUntil is { } active)
                return new LockoutStatus(true, active - now);

            // Attempts still being verified may all fail, so they consume the
            // remaining budget up front.
            if (policy.MaxAttempts > 0
                && state.ConsecutiveFailures + state.InFlight >= policy.MaxAttempts)
            {
                Lock(state, now, policy);
                return new LockoutStatus(true, policy.LockoutDuration);
            }

            state.InFlight++;
            return LockoutStatus.NotLocked;
        }
    }

    public void EndAttempt(string key)
    {
        if (string.IsNullOrEmpty(key) || !_states.TryGetValue(key, out var state))
            return;

        lock (state)
        {
            if (state.InFlight > 0)
                state.InFlight--;
        }
    }

    public LockoutStatus RegisterFailure(string key, LoginThrottlePolicy policy)
    {
        if (string.IsNullOrEmpty(key))
            return LockoutStatus.NotLocked;

        SweepIfNeeded(policy);
        var state = _states.GetOrAdd(key, static _ => new State());

        lock (state)
        {
            var now = _time.GetUtcNow();
            if (state.InFlight > 0)
                state.InFlight--;
            Refresh(state, now, policy);

            // Still locked → don't extend, just report remaining time.
            if (state.LockedUntil is { } active)
                return new LockoutStatus(true, active - now);

            state.ConsecutiveFailures++;
            state.LastFailureAt = now;

            if (policy.MaxAttempts > 0 && state.ConsecutiveFailures >= policy.MaxAttempts)
            {
                Lock(state, now, policy);
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

    /// <summary>Clears an elapsed lockout and decays failures older than the window.</summary>
    private static void Refresh(State state, DateTimeOffset now, LoginThrottlePolicy policy)
    {
        if (state.LockedUntil is { } until && now >= until)
        {
            state.LockedUntil = null;
            state.ConsecutiveFailures = 0;
        }
        if (state.LockedUntil is null
            && state.ConsecutiveFailures > 0
            && now - state.LastFailureAt >= policy.LockoutDuration)
            state.ConsecutiveFailures = 0;
    }

    private static void Lock(State state, DateTimeOffset now, LoginThrottlePolicy policy)
    {
        state.LockedUntil = now + policy.LockoutDuration;
        state.ConsecutiveFailures = 0;
    }

    /// <summary>
    /// Bounds memory: anonymous callers can create one entry per guessed
    /// username. Entries without a reservation, lock or recent failure carry no
    /// information and are removed.
    /// </summary>
    private void SweepIfNeeded(LoginThrottlePolicy policy)
    {
        var now = _time.GetUtcNow();
        if (policy.LockoutDuration > _longestWindow)
            _longestWindow = policy.LockoutDuration;
        if (_states.Count < SweepThreshold || now - _lastSweep < SweepInterval)
            return;
        _lastSweep = now;
        // ponytail: O(n) sweep at most once a minute; a timer wheel if key counts ever reach millions.
        foreach (var (key, state) in _states)
        {
            lock (state)
            {
                bool idle = state.InFlight == 0
                            && (state.LockedUntil is null || now >= state.LockedUntil)
                            && now - state.LastFailureAt >= _longestWindow;
                if (idle)
                    _states.TryRemove(new KeyValuePair<string, State>(key, state));
            }
        }
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
