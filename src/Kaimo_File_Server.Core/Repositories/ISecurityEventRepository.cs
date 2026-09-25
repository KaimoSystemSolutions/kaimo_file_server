using Kaimo_File_Server.Core.Domain.Identity;

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

    Task<SecurityTotals> GetTotalsAsync(DateTime sinceUtc, DateTime nowUtc);

    /// <summary>Accounts with the most failed attempts first.</summary>
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
