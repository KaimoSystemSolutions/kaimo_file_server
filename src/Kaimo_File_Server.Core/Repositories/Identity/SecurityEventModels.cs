namespace Kaimo_File_Server.Core.Repositories.Identity;

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
