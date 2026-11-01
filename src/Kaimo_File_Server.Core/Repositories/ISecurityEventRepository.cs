using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;

namespace Kaimo_File_Server.Core.Repositories;

/// <summary>Login attempts of one account name within a period.</summary>
public sealed record AccountLoginSummary(
    string Username,
    int Successes,
    int Failures,
    int Lockouts,
    int Addresses,
    DateTime LastAttemptUtc,
    string? LastAddress,
    DateTime? LockedUntilUtc);

/// <summary>API / WebDAV traffic of one client address within a period.</summary>
public sealed record ClientActivitySummary(
    string Address,
    long ApiRequests,
    long WebDavRequests,
    long RejectedRequests,
    DateTime FirstSeenUtc,
    DateTime LastSeenUtc,
    string LastPath);

/// <summary>Headline numbers of the security overview for a period.</summary>
public sealed record SecurityTotals(int Successes, int Failures, int ActiveLockouts, long Requests, long Rejected);

/// <summary>Persistent store behind the security overview: login attempts and hourly client activity.</summary>
public interface ISecurityEventRepository
{
    /// <summary>
    /// Appends the login attempts and adds the bucket counters to existing rows of the same
    /// address and hour (inserting new rows otherwise), in one transaction.
    /// </summary>
    Task SaveAsync(
        IReadOnlyCollection<LoginAttemptRecord> attempts,
        IReadOnlyCollection<ClientActivityBucket> buckets,
        CancellationToken ct = default);

    /// <summary>
    /// Adds repeated lockout hits to already saved attempts: <see cref="LoginAttemptRecord.RepeatCount"/>
    /// of each row id grows by the given amount, in one transaction. Unknown ids are ignored.
    /// </summary>
    Task AddLockoutRepeatsAsync(IReadOnlyDictionary<long, int> increments, CancellationToken ct = default);

    /// <summary>
    /// Saved attempts of <paramref name="channel"/> by any of <paramref name="usernames"/> or from
    /// any of <paramref name="addresses"/> (null matches attempts without an address) since
    /// <paramref name="sinceUtc"/>, plus older lockouts still running after it, oldest first. Lets a
    /// writer that receives events in batches (SMB) fold them into rows it saved earlier.
    /// </summary>
    Task<IReadOnlyList<LoginAttemptRecord>> ListRecentAsync(
        SecurityChannel channel, IReadOnlyCollection<string> usernames, IReadOnlyCollection<string?> addresses,
        DateTime sinceUtc, CancellationToken ct = default);

    /// <summary>
    /// End of the latest lockout of <paramref name="username"/> on <paramref name="channel"/> that
    /// began at or after <paramref name="sinceUtc"/> and is still running at <paramref name="nowUtc"/>;
    /// null when there is none.
    /// </summary>
    Task<DateTime?> GetActiveLockUntilAsync(SecurityChannel channel, string username, DateTime sinceUtc, DateTime nowUtc);

    /// <summary>
    /// Ends every lockout of <paramref name="username"/> on <paramref name="channel"/> that still runs
    /// at <paramref name="nowUtc"/> (an administrator lifted it), so the overview no longer counts it
    /// and a later lockout is recorded as a new one.
    /// </summary>
    Task EndActiveLocksAsync(SecurityChannel channel, string username, DateTime nowUtc);

    /// <summary>
    /// Totals of the period. Failures include attempts refused by a locked address;
    /// active lockouts count locked accounts only.
    /// </summary>
    Task<SecurityTotals> GetTotalsAsync(DateTime sinceUtc, DateTime nowUtc);

    /// <summary>
    /// Accounts with the most failed attempts first; lockouts count every folded repeat.
    /// Address lockouts are logged without an account and are not part of any summary.
    /// </summary>
    Task<IReadOnlyList<AccountLoginSummary>> SummarizeAccountsAsync(DateTime sinceUtc, DateTime nowUtc, int limit);

    /// <summary>Busiest clients first.</summary>
    Task<IReadOnlyList<ClientActivitySummary>> SummarizeClientsAsync(DateTime sinceUtc, int limit);

    /// <summary>
    /// Newest attempts first. <paramref name="search"/> matches part of the user name or address.
    /// </summary>
    Task<IReadOnlyList<LoginAttemptRecord>> ListLoginAttemptsAsync(
        DateTime sinceUtc, string? search, bool failedOnly, int limit);

    /// <summary>Deletes attempts and buckets older than <paramref name="cutoffUtc"/>; returns the count.</summary>
    Task<int> PruneOlderThanAsync(DateTime cutoffUtc);
}
