namespace Kaimo_File_Server.Core.Security.Authentication;

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
