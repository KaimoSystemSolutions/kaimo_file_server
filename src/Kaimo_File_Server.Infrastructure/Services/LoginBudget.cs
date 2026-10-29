using Kaimo_File_Server.Core.Security;

namespace Kaimo_File_Server.Infrastructure.Services;

/// <summary>
/// One login attempt across several <see cref="ILoginThrottle"/> keys at once (client address,
/// user name + address, account). <see cref="Begin"/> reserves the attempt on every key in the
/// given order and stops at the first locked one, releasing what it already reserved, so an
/// earlier key (the address) can refuse an attempt before later keys are even touched. Settle
/// a successful <see cref="Begin"/> with exactly one of <see cref="RegisterFailure"/>,
/// <see cref="Reset"/> or <see cref="Release"/>. Empty keys are skipped by the throttle.
/// Each key carries the <see cref="LockoutScope"/> its lock is reported with.
/// </summary>
internal sealed class LoginBudget(
    ILoginThrottle throttle, params (string Key, LoginThrottlePolicy Policy, LockoutScope Scope)[] entries)
{
    /// <summary>Reserves the attempt; when a key is locked, its status and scope.</summary>
    public (LockoutStatus Status, LockoutScope Scope) Begin()
    {
        for (int i = 0; i < entries.Length; i++)
        {
            var status = throttle.BeginAttempt(entries[i].Key, entries[i].Policy);
            if (!status.IsLockedOut)
                continue;

            for (int reserved = 0; reserved < i; reserved++)
                throttle.EndAttempt(entries[reserved].Key);
            return (status, entries[i].Scope);
        }
        return (LockoutStatus.NotLocked, LockoutScope.Account);
    }

    /// <summary>
    /// Counts the failure on every key; locked when any key reached its limit, with the longest
    /// remaining lock. The scope is <see cref="LockoutScope.Account"/> as soon as any account key
    /// is locked, so a lock of the account is never hidden behind a lock of the address.
    /// </summary>
    public (LockoutStatus Status, LockoutScope Scope) RegisterFailure()
    {
        var result = LockoutStatus.NotLocked;
        var scope = LockoutScope.Address;
        foreach (var (key, policy, keyScope) in entries)
        {
            var status = throttle.RegisterFailure(key, policy);
            if (!status.IsLockedOut)
                continue;
            if (keyScope == LockoutScope.Account)
                scope = LockoutScope.Account;
            if (!result.IsLockedOut || status.RetryAfter > result.RetryAfter)
                result = status;
        }
        return (result, result.IsLockedOut ? scope : LockoutScope.Account);
    }

    /// <summary>Clears every key (successful login).</summary>
    public void Reset()
    {
        foreach (var (key, _, _) in entries)
            throttle.Reset(key);
    }

    /// <summary>Releases the reservations without counting the attempt.</summary>
    public void Release()
    {
        foreach (var (key, _, _) in entries)
            throttle.EndAttempt(key);
    }
}
