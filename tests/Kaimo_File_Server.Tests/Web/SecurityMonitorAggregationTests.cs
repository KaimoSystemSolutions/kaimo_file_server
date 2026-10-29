using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Repositories;
using Kaimo_File_Server.Tests.Infrastructure;
using Kaimo_File_Server.Web.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Every attempt during a lockout returns LockedOut again, so a client that keeps retrying
/// would write one row per request. The monitor folds those repeats into the row of the first
/// LockedOut attempt of the same lock (same channel, account and address) and counts them in
/// <see cref="LoginAttemptRecord.RepeatCount"/>; the overview sums that count. Runs on the
/// real EF schema (in-memory SQLite).
/// </summary>
public sealed class SecurityMonitorAggregationTests : DatabaseTestBase
{
    private static readonly TimeSpan Lock = TimeSpan.FromMinutes(15);

    private readonly MutableTimeProvider _time = new();
    private readonly SecurityMonitor _monitor;
    private readonly FlakyRepository _repo;

    public SecurityMonitorAggregationTests()
    {
        _monitor = new SecurityMonitor(timeProvider: _time);
        _repo = new FlakyRepository(new SecurityEventRepository(DbFactory));
    }

    private DateTime Since => _time.Now.UtcDateTime.AddDays(-1);
    private DateTime NowUtc => _time.Now.UtcDateTime;

    /// <summary>Records <paramref name="count"/> LockedOut attempts one second apart, all inside the lock ending at <paramref name="lockedUntil"/>.</summary>
    private void RecordLocked(int count, DateTimeOffset lockedUntil, string user = "admin",
        string address = "10.0.0.1", SecurityChannel channel = SecurityChannel.Web)
    {
        for (int i = 0; i < count; i++)
        {
            _monitor.RecordLogin(channel, user, address, LoginResult.LockedOut(lockedUntil - _time.Now));
            _time.Advance(TimeSpan.FromSeconds(1));
        }
    }

    private async Task<List<LoginAttemptRecord>> RowsAsync()
    {
        await using var db = NewContext();
        return await db.LoginAttempts.OrderBy(a => a.Id).ToListAsync();
    }

    [Fact]
    public async Task RepeatedLockedOutAttempts_DuringOneLock_AreStoredAsOneRow()
    {
        RecordLocked(10, _time.Now + Lock);

        await _monitor.FlushAsync(_repo);

        var row = Assert.Single(await RowsAsync());
        Assert.Equal(LoginOutcome.LockedOut, row.Outcome);
        Assert.Equal(10, row.RepeatCount);
    }

    [Fact]
    public async Task Overview_CountsEveryRepeat()
    {
        _monitor.RecordLogin(SecurityChannel.Web, "admin", "10.0.0.1", LoginResult.InvalidCredentials);
        RecordLocked(10, _time.Now + Lock);
        await _monitor.FlushAsync(_repo);

        var totals = await _repo.GetTotalsAsync(Since, NowUtc);
        Assert.Equal(11, totals.Failures);
        Assert.Equal(1, totals.ActiveLockouts);

        var admin = Assert.Single(await _repo.SummarizeAccountsAsync(Since, NowUtc, 10));
        Assert.Equal(1, admin.Failures);
        Assert.Equal(10, admin.Lockouts);

        var attempts = await _repo.ListLoginAttemptsAsync(Since, null, failedOnly: true, 10);
        Assert.Equal(2, attempts.Count);
        Assert.Equal(10, attempts[0].RepeatCount); // newest first
        Assert.Equal(1, attempts[1].RepeatCount);
    }

    [Fact]
    public async Task RepeatsAfterTheFirstFlush_AreAddedToTheSavedRow()
    {
        var until = _time.Now + Lock;
        RecordLocked(1, until);
        await _monitor.FlushAsync(_repo);

        RecordLocked(9, until);
        await _monitor.FlushAsync(_repo);

        var row = Assert.Single(await RowsAsync());
        Assert.Equal(10, row.RepeatCount);
    }

    [Fact]
    public async Task ANewLock_StartsANewRow()
    {
        RecordLocked(3, _time.Now + Lock);
        await _monitor.FlushAsync(_repo);

        _time.Advance(Lock);
        RecordLocked(2, _time.Now + Lock);
        await _monitor.FlushAsync(_repo);

        var rows = await RowsAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(3, rows[0].RepeatCount);
        Assert.Equal(2, rows[1].RepeatCount);
    }

    [Fact]
    public async Task DifferentAddressChannelOrAccount_AreNotMerged()
    {
        var until = _time.Now + Lock;
        RecordLocked(2, until);
        RecordLocked(2, until, address: "10.0.0.2");
        RecordLocked(2, until, channel: SecurityChannel.Api);
        RecordLocked(2, until, user: "alice");

        await _monitor.FlushAsync(_repo);

        var rows = await RowsAsync();
        Assert.Equal(4, rows.Count);
        Assert.All(rows, r => Assert.Equal(2, r.RepeatCount));
    }

    [Fact]
    public async Task OtherOutcomes_AreNeverMerged()
    {
        for (int i = 0; i < 3; i++)
            _monitor.RecordLogin(SecurityChannel.Web, "admin", "10.0.0.1", LoginResult.InvalidCredentials);

        await _monitor.FlushAsync(_repo);

        var rows = await RowsAsync();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(1, r.RepeatCount));
    }

    [Fact]
    public async Task FailedSave_KeepsTheRepeats_ForTheNextFlush()
    {
        var until = _time.Now + Lock;
        RecordLocked(3, until);
        _repo.FailNextSave = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _monitor.FlushAsync(_repo));

        RecordLocked(2, until);
        await _monitor.FlushAsync(_repo);

        var row = Assert.Single(await RowsAsync());
        Assert.Equal(5, row.RepeatCount);
    }

    [Fact]
    public async Task FailedRepeatUpdate_IsRetried_WithoutDoubleCounting()
    {
        var until = _time.Now + Lock;
        RecordLocked(1, until);
        await _monitor.FlushAsync(_repo);

        RecordLocked(4, until);
        _repo.FailNextRepeats = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _monitor.FlushAsync(_repo));

        await _monitor.FlushAsync(_repo);
        await _monitor.FlushAsync(_repo);

        var row = Assert.Single(await RowsAsync());
        Assert.Equal(5, row.RepeatCount);
    }

    /// <summary>Records one address-scoped LockedOut attempt per user name, one second apart, inside the lock ending at <paramref name="lockedUntil"/>.</summary>
    private void RecordAddressLocked(DateTimeOffset lockedUntil, string address, params string[] users)
    {
        foreach (var user in users)
        {
            _monitor.RecordLogin(SecurityChannel.Web, user, address,
                LoginResult.LockedOut(lockedUntil - _time.Now, LockoutScope.Address));
            _time.Advance(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task AddressLockout_IsStoredWithoutAccount_AndFoldedAcrossUsernames()
    {
        RecordAddressLocked(_time.Now + Lock, "10.0.0.9", "guess1", "admin", "guess2");

        await _monitor.FlushAsync(_repo);

        var row = Assert.Single(await RowsAsync());
        Assert.Equal(LoginOutcome.AddressLockedOut, row.Outcome);
        Assert.Equal("", row.Username);
        Assert.Equal("10.0.0.9", row.Address);
        Assert.NotNull(row.LockedUntilUtc);
        Assert.Equal(3, row.RepeatCount);
    }

    [Fact]
    public async Task Overview_DoesNotCountAddressLockoutsAsLockedAccounts()
    {
        _monitor.RecordLogin(SecurityChannel.Web, "guess0", "10.0.0.9", LoginResult.InvalidCredentials);
        RecordAddressLocked(_time.Now + Lock, "10.0.0.9", "admin", "guess1", "guess2", "guess3");
        await _monitor.FlushAsync(_repo);

        var totals = await _repo.GetTotalsAsync(Since, NowUtc);
        Assert.Equal(5, totals.Failures);
        Assert.Equal(0, totals.ActiveLockouts);

        var account = Assert.Single(await _repo.SummarizeAccountsAsync(Since, NowUtc, 10));
        Assert.Equal("guess0", account.Username);
        Assert.Equal(0, account.Lockouts);
        Assert.Null(account.LockedUntilUtc);
    }

    [Fact]
    public async Task AddressAndAccountLockouts_OfOneAddress_AreNotMerged()
    {
        var until = _time.Now + Lock;
        RecordLocked(2, until, address: "10.0.0.9");
        RecordAddressLocked(until, "10.0.0.9", "admin", "guess1");

        await _monitor.FlushAsync(_repo);

        var rows = await RowsAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(LoginOutcome.LockedOut, rows[0].Outcome);
        Assert.Equal(LoginOutcome.AddressLockedOut, rows[1].Outcome);
        Assert.All(rows, r => Assert.Equal(2, r.RepeatCount));
    }

    [Fact]
    public async Task ReadOnlyDemo_PersistsNoRepeats()
    {
        var demo = new SecurityMonitor(new DemoModeOptions { ReadOnly = true }, _time);
        demo.RecordLogin(SecurityChannel.Web, "admin", "10.0.0.1", LoginResult.LockedOut(Lock));
        demo.RecordLogin(SecurityChannel.Web, "admin", "10.0.0.1", LoginResult.LockedOut(Lock));

        await demo.FlushAsync(_repo);

        Assert.Empty(await RowsAsync());
    }

    /// <summary>The real repository, with one-shot failures of the attempt insert or the repeat update.</summary>
    private sealed class FlakyRepository(ISecurityEventRepository inner) : ISecurityEventRepository
    {
        public bool FailNextSave { get; set; }
        public bool FailNextRepeats { get; set; }

        public Task SaveAsync(IReadOnlyCollection<LoginAttemptRecord> attempts,
            IReadOnlyCollection<ClientActivityBucket> buckets, CancellationToken ct = default)
        {
            if (!FailNextSave) return inner.SaveAsync(attempts, buckets, ct);
            FailNextSave = false;
            throw new InvalidOperationException("database down");
        }

        public Task AddLockoutRepeatsAsync(IReadOnlyDictionary<long, int> increments, CancellationToken ct = default)
        {
            if (!FailNextRepeats) return inner.AddLockoutRepeatsAsync(increments, ct);
            FailNextRepeats = false;
            throw new InvalidOperationException("database down");
        }

        public Task<SecurityTotals> GetTotalsAsync(DateTime sinceUtc, DateTime nowUtc) => inner.GetTotalsAsync(sinceUtc, nowUtc);

        public Task<IReadOnlyList<AccountLoginSummary>> SummarizeAccountsAsync(DateTime sinceUtc, DateTime nowUtc, int limit)
            => inner.SummarizeAccountsAsync(sinceUtc, nowUtc, limit);

        public Task<IReadOnlyList<ClientActivitySummary>> SummarizeClientsAsync(DateTime sinceUtc, int limit)
            => inner.SummarizeClientsAsync(sinceUtc, limit);

        public Task<IReadOnlyList<LoginAttemptRecord>> ListLoginAttemptsAsync(DateTime sinceUtc, string? search, bool failedOnly, int limit)
            => inner.ListLoginAttemptsAsync(sinceUtc, search, failedOnly, limit);

        public Task<int> PruneOlderThanAsync(DateTime cutoffUtc) => inner.PruneOlderThanAsync(cutoffUtc);
    }
}
