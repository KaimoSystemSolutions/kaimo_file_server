using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Folds repeated LockedOut attempts of one lock into a single <see cref="LoginAttemptRecord"/>.
/// A client that keeps retrying during a lockout gets LockedOut on every request; instead of one
/// row each, the first LockedOut attempt of a lock (same channel, account and address, same end
/// of the lock; address locks are logged without an account and fold across user names) is stored and every further hit raises its <see cref="LoginAttemptRecord.RepeatCount"/>
/// — in the batch while the row is unsaved, afterwards as an increment of the saved row.
///
/// Used only by <see cref="SecurityMonitor.FlushAsync"/>, whose flushes are serialized, so it
/// needs no locking. Per process: after a restart a running lock starts a new row.
/// </summary>
internal sealed class LockoutRepeatFolder
{
    /// <summary>
    /// The lock end is derived from the remaining time a moment after the throttle computed it,
    /// so hits of one lock differ by a few milliseconds; locks ending further apart are distinct.
    /// </summary>
    public static readonly TimeSpan SameLockTolerance = TimeSpan.FromSeconds(5);

    private sealed class OpenLock(DateTime lockedUntilUtc, LoginAttemptRecord pending)
    {
        public DateTime LockedUntilUtc { get; } = lockedUntilUtc;

        /// <summary>The batch row of this lock while it is unsaved.</summary>
        public LoginAttemptRecord? Pending { get; set; } = pending;

        /// <summary>Id of the saved row once the batch is written.</summary>
        public long RowId { get; set; }
    }

    private readonly Dictionary<(SecurityChannel Channel, LoginOutcome Outcome, string Username, string? Address), OpenLock> _open = new();
    private readonly Dictionary<long, int> _unsavedRepeats = new();

    /// <summary>Increments for saved rows that are not yet written.</summary>
    public IReadOnlyDictionary<long, int> PendingRepeats => _unsavedRepeats;

    /// <summary>
    /// Returns the rows to insert: every attempt except LockedOut hits of a lock that already
    /// has a row, which are counted into that row instead.
    /// </summary>
    public List<LoginAttemptRecord> Fold(IEnumerable<LoginAttemptRecord> attempts)
    {
        var rows = new List<LoginAttemptRecord>();
        foreach (var attempt in attempts)
        {
            if (!IsLockout(attempt.Outcome) || attempt.LockedUntilUtc is not { } until)
            {
                rows.Add(attempt);
                continue;
            }

            var key = (attempt.Channel, attempt.Outcome, attempt.Username, attempt.Address);
            if (_open.TryGetValue(key, out var open) && (until - open.LockedUntilUtc).Duration() <= SameLockTolerance)
            {
                if (open.Pending is { } pending)
                    pending.RepeatCount += attempt.RepeatCount;
                else
                    _unsavedRepeats[open.RowId] = _unsavedRepeats.GetValueOrDefault(open.RowId) + attempt.RepeatCount;
                continue;
            }

            _open[key] = new OpenLock(until, attempt);
            rows.Add(attempt);
        }
        return rows;
    }

    private static bool IsLockout(LoginOutcome outcome)
        => outcome is LoginOutcome.LockedOut or LoginOutcome.AddressLockedOut;

    /// <summary>The batch from <see cref="Fold"/> was written: later hits go to the saved rows.</summary>
    public void BatchSaved(DateTime nowUtc)
    {
        foreach (var (key, open) in _open.ToList())
        {
            if (open.Pending is { } pending)
            {
                open.RowId = pending.Id;
                open.Pending = null;
            }

            // A repository that reports no ids cannot take increments; an ended lock takes no more hits.
            if (open.RowId == 0 || open.LockedUntilUtc + SameLockTolerance < nowUtc)
                _open.Remove(key);
        }

        // ponytail: bounded like the monitor's queue; clearing only splits a running lock into two rows.
        if (_open.Count > SecurityMonitor.MaxPending)
            _open.Clear();
    }

    /// <summary>
    /// The batch from <see cref="Fold"/> was not written. Its rows are re-queued with their counts
    /// and folded again by the next flush, so locks that pointed at them are forgotten.
    /// </summary>
    public void BatchFailed()
    {
        foreach (var (key, open) in _open.ToList())
            if (open.Pending is not null)
                _open.Remove(key);
    }

    /// <summary>The <see cref="PendingRepeats"/> were written.</summary>
    public void RepeatsSaved() => _unsavedRepeats.Clear();
}
