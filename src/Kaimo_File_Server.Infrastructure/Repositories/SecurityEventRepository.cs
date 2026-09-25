using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="ISecurityEventRepository"/>.</summary>
public sealed class SecurityEventRepository(IDbContextFactory<ApplicationDbContext> dbFactory)
    : ISecurityEventRepository
{
    public async Task SaveAsync(
        IReadOnlyCollection<LoginAttemptRecord> attempts,
        IReadOnlyCollection<ClientActivityBucket> buckets,
        CancellationToken ct = default)
    {
        if (attempts.Count == 0 && buckets.Count == 0)
            return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.LoginAttempts.AddRange(attempts);

        if (buckets.Count > 0)
        {
            // Single writer (the monitor's flush is serialized), so read-then-update cannot race.
            var hours = buckets.Select(b => b.HourUtc).Distinct().ToList();
            var addresses = buckets.Select(b => b.Address).Distinct().ToList();
            var existing = (await db.ClientActivityBuckets
                    .Where(b => hours.Contains(b.HourUtc) && addresses.Contains(b.Address))
                    .ToListAsync(ct))
                .ToDictionary(b => (b.Address, b.HourUtc.Ticks));

            foreach (var bucket in buckets)
            {
                if (!existing.TryGetValue((bucket.Address, bucket.HourUtc.Ticks), out var row))
                {
                    db.ClientActivityBuckets.Add(bucket);
                    continue;
                }

                row.ApiRequests += bucket.ApiRequests;
                row.WebDavRequests += bucket.WebDavRequests;
                row.RejectedRequests += bucket.RejectedRequests;
                if (bucket.LastSeenUtc >= row.LastSeenUtc)
                {
                    row.LastSeenUtc = bucket.LastSeenUtc;
                    row.LastPath = bucket.LastPath;
                }
            }
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<SecurityTotals> GetTotalsAsync(DateTime sinceUtc, DateTime nowUtc)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var attempts = db.LoginAttempts.Where(a => a.AtUtc >= sinceUtc);
        var buckets = db.ClientActivityBuckets.Where(b => b.LastSeenUtc >= sinceUtc);

        int successes = await attempts.CountAsync(a => a.Outcome == LoginOutcome.Success);
        int failures = await attempts.CountAsync(a => a.Outcome != LoginOutcome.Success);
        int activeLockouts = await db.LoginAttempts
            .Where(a => a.LockedUntilUtc > nowUtc)
            .Select(a => a.Username)
            .Distinct()
            .CountAsync();
        long requests = await buckets.SumAsync(b => b.ApiRequests + b.WebDavRequests);
        long rejected = await buckets.SumAsync(b => b.RejectedRequests);

        return new SecurityTotals(successes, failures, activeLockouts, requests, rejected);
    }

    public async Task<IReadOnlyList<AccountLoginSummary>> SummarizeAccountsAsync(
        DateTime sinceUtc, DateTime nowUtc, int limit)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var inPeriod = db.LoginAttempts.Where(a => a.AtUtc >= sinceUtc);

        var rows = await inPeriod
            .GroupBy(a => a.Username)
            .Select(g => new
            {
                Username = g.Key,
                Successes = g.Count(a => a.Outcome == LoginOutcome.Success),
                Failures = g.Count(a => a.Outcome == LoginOutcome.InvalidCredentials
                                        || a.Outcome == LoginOutcome.AccountDisabled),
                Lockouts = g.Count(a => a.Outcome == LoginOutcome.LockedOut),
                Addresses = g.Select(a => a.Address).Distinct().Count(),
                LastAttemptUtc = g.Max(a => a.AtUtc),
                LockedUntilUtc = g.Max(a => a.LockedUntilUtc),
            })
            .OrderByDescending(r => r.Failures + r.Lockouts)
            .ThenByDescending(r => r.LastAttemptUtc)
            .Take(limit)
            .ToListAsync();

        var names = rows.Select(r => r.Username).ToList();
        var lastAddress = (await inPeriod
                .Where(a => names.Contains(a.Username))
                .GroupBy(a => a.Username)
                .Select(g => g.OrderByDescending(a => a.AtUtc).ThenByDescending(a => a.Id).First())
                .ToListAsync())
            .ToDictionary(a => a.Username, a => a.Address);

        return rows
            .Select(r => new AccountLoginSummary(
                r.Username, r.Successes, r.Failures, r.Lockouts, r.Addresses,
                AsUtc(r.LastAttemptUtc),
                lastAddress.GetValueOrDefault(r.Username),
                r.LockedUntilUtc is { } until && AsUtc(until) > nowUtc ? AsUtc(until) : null))
            // Accounts that are locked right now first.
            .OrderByDescending(r => r.LockedUntilUtc is not null)
            .ToList();
    }

    public async Task<IReadOnlyList<ClientActivitySummary>> SummarizeClientsAsync(DateTime sinceUtc, int limit)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var inPeriod = db.ClientActivityBuckets.Where(b => b.LastSeenUtc >= sinceUtc);

        var rows = await inPeriod
            .GroupBy(b => b.Address)
            .Select(g => new
            {
                Address = g.Key,
                Api = g.Sum(b => b.ApiRequests),
                WebDav = g.Sum(b => b.WebDavRequests),
                Rejected = g.Sum(b => b.RejectedRequests),
                FirstSeenUtc = g.Min(b => b.HourUtc),
                LastSeenUtc = g.Max(b => b.LastSeenUtc),
            })
            .OrderByDescending(r => r.Api + r.WebDav)
            .Take(limit)
            .ToListAsync();

        var addresses = rows.Select(r => r.Address).ToList();
        var lastPath = (await inPeriod
                .Where(b => addresses.Contains(b.Address))
                .GroupBy(b => b.Address)
                .Select(g => g.OrderByDescending(b => b.LastSeenUtc).First())
                .ToListAsync())
            .ToDictionary(b => b.Address, b => b.LastPath);

        return rows
            .Select(r => new ClientActivitySummary(
                r.Address, r.Api, r.WebDav, r.Rejected,
                AsUtc(r.FirstSeenUtc), AsUtc(r.LastSeenUtc),
                lastPath.GetValueOrDefault(r.Address, "")))
            .ToList();
    }

    public async Task<IReadOnlyList<LoginAttemptRecord>> ListLoginAttemptsAsync(
        DateTime sinceUtc, string? search, bool failedOnly, int limit)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var query = db.LoginAttempts.AsNoTracking().Where(a => a.AtUtc >= sinceUtc);

        if (failedOnly)
            query = query.Where(a => a.Outcome != LoginOutcome.Success);

        var term = search?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(term))
            query = query.Where(a => a.Username.Contains(term)
                                     || (a.Address != null && a.Address.ToLower().Contains(term)));

        var list = await query
            .OrderByDescending(a => a.AtUtc)
            .ThenByDescending(a => a.Id)
            .Take(limit)
            .ToListAsync();
        foreach (var a in list)
        {
            a.AtUtc = AsUtc(a.AtUtc);
            if (a.LockedUntilUtc is { } until)
                a.LockedUntilUtc = AsUtc(until);
        }
        return list;
    }

    public async Task<int> PruneOlderThanAsync(DateTime cutoffUtc)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        int attempts = await db.LoginAttempts.Where(a => a.AtUtc < cutoffUtc).ExecuteDeleteAsync();
        int buckets = await db.ClientActivityBuckets.Where(b => b.LastSeenUtc < cutoffUtc).ExecuteDeleteAsync();
        return attempts + buckets;
    }

    // SQLite hands DateTime back as Unspecified; PostgreSQL already returns UTC.
    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
