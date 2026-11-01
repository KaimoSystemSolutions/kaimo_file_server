using System.Text.RegularExpressions;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services.Notifications;
using Kaimo_File_Server.Infrastructure.Configuration;

namespace Kaimo_File_Server.SmbBridge.Services;

/// <summary>
/// Writes reported SMB logins into the security overview (channel <see cref="SecurityChannel.Smb"/>):
/// <list type="bullet">
///   <item>successes and failures as counters: one row per account, address, outcome and hour,
///     whose <see cref="LoginAttemptRecord.RepeatCount"/> grows;</item>
///   <item>at most <see cref="MaxFailureRowsPerAddressHour"/> failure rows per address and hour;
///     failures of further accounts are counted in one row of that address without an account,
///     so a user-name spray (which Samba never locks) cannot grow the table without bound;</item>
///   <item>LockedOut answers folded into the row of their lock (per account and address), which
///     ends <see cref="LoginProtectionSettings.SmbLockoutMinutes"/> after its first LockedOut;
///     LockedOut for an unknown account is dropped, since Samba answers it only for passdb
///     accounts and it can only come from a forged log line;</item>
///   <item>LockedOut within <see cref="UnlockGrace"/> after an unlock request dropped (Samba has not
///     applied it yet), and a success ends the account's running lock (Samba admitted the login);</item>
///   <item>one <c>security.account_locked</c> notification per account lock, like web logins.</item>
/// </list>
/// Samba enforces the lockout itself; this only reports it. Batches are serialized, so reading
/// the rows to fold into and writing them cannot race within the bridge.
/// </summary>
public sealed partial class SmbAuthenticationRecorder(
    ISecurityEventRepository events,
    IUserRepository users,
    IConfigRepository config,
    INotificationPublisher notifications,
    TimeProvider? timeProvider = null)
{
    /// <summary>Failure rows one address may start per hour before its failures share one row.</summary>
    public const int MaxFailureRowsPerAddressHour = 20;

    /// <summary>
    /// How long after an unlock request LockedOut answers still count as the lifted lock: Samba
    /// applies the unlock with its next user sync (default every 60 s) and refuses logins until then.
    /// </summary>
    // ponytail: fixed window, not the Samba sync interval; a real new lock within it shows only
    // with its next LockedOut after the window (or never, if the client stops retrying).
    public static readonly TimeSpan UnlockGrace = TimeSpan.FromMinutes(3);

    private const int MaxUsernameLength = 256;
    private static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxEventAge = TimeSpan.FromDays(1);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    [GeneratedRegex("^NT_STATUS_[A-Z0-9_]{1,64}$")]
    private static partial Regex NtStatus();

    /// <summary>Records the batch; returns how many events were usable.</summary>
    public async Task<int> RecordAsync(IReadOnlyList<SmbAuthenticationEvent> batch, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var attempts = batch.Select(e => ToAttempt(e, now)).OfType<LoginAttemptRecord>().OrderBy(a => a.AtUtc).ToList();
        var lockedUsers = await ResolveLockedAccountsAsync(attempts);
        attempts.RemoveAll(a => a.Outcome == LoginOutcome.LockedOut
                                && (lockedUsers[a.Username] is not { } user || IsUnlockEcho(a, user)));
        if (attempts.Count == 0) return 0;

        var lockFor = TimeSpan.FromMinutes((await config.LoadLoginProtectionAsync()).SmbLockoutMinutes);
        await Gate.WaitAsync(ct);
        try
        {
            // Counters fold within their hour; older rows only matter while their lock still runs.
            var known = (await events.ListRecentAsync(
                SecurityChannel.Smb,
                attempts.Select(a => a.Username).Distinct().ToList(),
                attempts.Select(a => a.Address).Distinct().ToList(),
                Hour(attempts[0].AtUtc), ct)).ToList();

            var inserts = new List<LoginAttemptRecord>();
            var increments = new Dictionary<long, int>();
            var newLocks = new List<LoginAttemptRecord>();
            var endedLocks = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            foreach (var attempt in attempts)
            {
                if (attempt.Outcome == LoginOutcome.Success) EndLocksBefore(known, attempt, endedLocks);
                if (IsFailure(attempt.Outcome)) ApplyFailureBudget(known, attempt);
                var row = FoldTarget(known, attempt);
                if (row is not null)
                {
                    if (row.Id == 0) row.RepeatCount++;
                    else increments[row.Id] = increments.GetValueOrDefault(row.Id) + 1;
                    continue;
                }

                if (attempt.Outcome == LoginOutcome.LockedOut)
                {
                    attempt.LockedUntilUtc = attempt.AtUtc + lockFor;
                    // Samba locks the account, not the address: notify once per account lock.
                    if (!known.Any(r => r.Outcome == LoginOutcome.LockedOut && r.Username == attempt.Username && r.LockedUntilUtc > attempt.AtUtc))
                        newLocks.Add(attempt);
                }
                inserts.Add(attempt);
                known.Add(attempt);
            }

            // Before the inserts, so a lock that began after the success in this batch keeps running.
            foreach (var (username, at) in endedLocks)
                await events.EndActiveLocksAsync(SecurityChannel.Smb, username, at);
            // ponytail: separate writes, not one transaction; a failure between them makes the
            // retried batch insert its rows again (an over-count in the overview, never a loss).
            await events.SaveAsync(inserts, [], ct);
            await events.AddLockoutRepeatsAsync(increments, ct);

            foreach (var locked in newLocks)
                await NotifyLockedAsync(locked, lockedUsers[locked.Username]!, lockFor);
            return attempts.Count;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>The Kaimo account of every LockedOut user name in the batch; null when there is none.</summary>
    private async Task<Dictionary<string, User?>> ResolveLockedAccountsAsync(List<LoginAttemptRecord> attempts)
    {
        var accounts = new Dictionary<string, User?>(StringComparer.Ordinal);
        foreach (var name in attempts.Where(a => a.Outcome == LoginOutcome.LockedOut).Select(a => a.Username).Distinct())
            accounts[name] = await users.GetByUsernameIgnoreCaseAsync(name);
        return accounts;
    }

    /// <summary>
    /// A failure of an account without a row of its own this hour, from an address that already
    /// started <see cref="MaxFailureRowsPerAddressHour"/> failure rows, is counted in the address row.
    /// </summary>
    private static void ApplyFailureBudget(List<LoginAttemptRecord> known, LoginAttemptRecord attempt)
    {
        var hour = Hour(attempt.AtUtc);
        if (known.Any(r => r.Outcome == attempt.Outcome && SameSource(r, attempt) && Hour(r.AtUtc) == hour)) return;
        int rows = known.Count(r => IsFailure(r.Outcome) && r.Username.Length > 0
                                    && r.Address == attempt.Address && Hour(r.AtUtc) == hour);
        if (rows < MaxFailureRowsPerAddressHour) return;

        attempt.Username = string.Empty;
        attempt.Outcome = LoginOutcome.InvalidCredentials;
    }

    /// <summary>A LockedOut answer Samba gave before it applied the account's unlock request.</summary>
    private static bool IsUnlockEcho(LoginAttemptRecord attempt, User user)
        => user.SmbUnlockRequestedAtUtc is { } requested
           && attempt.AtUtc >= requested && attempt.AtUtc < requested + UnlockGrace;

    /// <summary>
    /// Samba admits a login only while the account is not locked: a success ends the account's
    /// locks that began before it. Stored rows are ended in the database (earliest success per account).
    /// </summary>
    private static void EndLocksBefore(List<LoginAttemptRecord> known, LoginAttemptRecord success, Dictionary<string, DateTime> endedLocks)
    {
        foreach (var row in known.Where(r => r.Outcome == LoginOutcome.LockedOut && r.Username == success.Username
                                             && r.AtUtc <= success.AtUtc && r.LockedUntilUtc > success.AtUtc))
        {
            row.LockedUntilUtc = success.AtUtc;
            if (row.Id != 0 && !endedLocks.ContainsKey(row.Username)) endedLocks[row.Username] = success.AtUtc;
        }
    }

    private static LoginAttemptRecord? FoldTarget(List<LoginAttemptRecord> known, LoginAttemptRecord attempt)
        => attempt.Outcome == LoginOutcome.LockedOut
            ? known.LastOrDefault(r => r.Outcome == LoginOutcome.LockedOut && SameSource(r, attempt) && r.LockedUntilUtc > attempt.AtUtc)
            : known.LastOrDefault(r => r.Outcome == attempt.Outcome && SameSource(r, attempt) && Hour(r.AtUtc) == Hour(attempt.AtUtc));

    private async Task NotifyLockedAsync(LoginAttemptRecord locked, User user, TimeSpan lockFor)
    {
        // A notification problem must never fail the batch (the publisher itself never throws).
        try
        {
            await notifications.PublishAsync(NotificationEvents.AccountLocked(user.Id, locked.Username, locked.Address, lockFor));
        }
        catch (Exception) { /* the event row is already saved */ }
    }

    private static LoginAttemptRecord? ToAttempt(SmbAuthenticationEvent e, DateTime now)
    {
        var username = (e.Username ?? "").Trim().ToLowerInvariant();
        if (username.Length == 0 || e.Status is null || !NtStatus().IsMatch(e.Status)) return null;

        var at = e.AtUtc.ToUniversalTime();
        // A wrong clock in either container must not place events outside the overview's periods.
        if (at > now + MaxClockSkew || at < now - MaxEventAge) at = now;

        return new LoginAttemptRecord
        {
            AtUtc = at,
            Channel = SecurityChannel.Smb,
            Username = username.Length <= MaxUsernameLength ? username : username[..MaxUsernameLength],
            Address = AccessRestrictionSettings.ParseAddress(e.Address)?.ToString(),
            Outcome = e.Status switch
            {
                "NT_STATUS_OK" => LoginOutcome.Success,
                "NT_STATUS_ACCOUNT_LOCKED_OUT" => LoginOutcome.LockedOut,
                "NT_STATUS_ACCOUNT_DISABLED" => LoginOutcome.AccountDisabled,
                _ => LoginOutcome.InvalidCredentials,
            },
        };
    }

    private static bool IsFailure(LoginOutcome outcome)
        => outcome is LoginOutcome.InvalidCredentials or LoginOutcome.AccountDisabled;

    private static bool SameSource(LoginAttemptRecord a, LoginAttemptRecord b)
        => a.Username == b.Username && a.Address == b.Address;

    private static DateTime Hour(DateTime value) => new(value.Ticks - value.Ticks % TimeSpan.TicksPerHour, DateTimeKind.Utc);
}
