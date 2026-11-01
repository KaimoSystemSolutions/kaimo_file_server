using Grpc.Core;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Domain.Notifications;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services.Notifications;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Infrastructure.Repositories;
using Kaimo_File_Server.SmbBridge.Grpc;
using Kaimo_File_Server.SmbBridge.Security;
using Kaimo_File_Server.SmbBridge.Services;
using Kaimo_File_Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// SMB logins reported by the Samba container (AP8d) land in the security overview: failures and
/// successes as a counter per account, address and hour (failures of one address capped, so a
/// user-name spray cannot grow the table without bound), repeated LockedOut answers folded into
/// the row of their lock, LockedOut only for existing accounts, and one AccountLocked
/// notification per account lock. Runs on the real EF schema (in-memory SQLite).
/// </summary>
public sealed class SmbAuthenticationRecorderTests : DatabaseTestBase
{
    private readonly MutableTimeProvider _time = new();
    private readonly SecurityEventRepository _repo;
    private readonly Mock<IUserRepository> _users = new();
    private readonly Mock<INotificationPublisher> _notifications = new();
    private readonly List<NotificationEvent> _published = new();
    private readonly User _anna = new(Guid.NewGuid(), "Anna", "anna.weber", "pw", "nt");

    public SmbAuthenticationRecorderTests()
    {
        _repo = new SecurityEventRepository(DbFactory);
        _users.Setup(u => u.GetByUsernameIgnoreCaseAsync(It.IsAny<string>()))
            .ReturnsAsync((string name) => string.Equals(name, _anna.Username, StringComparison.OrdinalIgnoreCase) ? _anna : null);
        _notifications.Setup(n => n.PublishAsync(It.IsAny<NotificationEvent>(), It.IsAny<CancellationToken>()))
            .Callback((NotificationEvent e, CancellationToken _) => _published.Add(e))
            .Returns(Task.CompletedTask);
    }

    private DateTime Now => _time.Now.UtcDateTime;

    private SmbAuthenticationRecorder Recorder(int lockoutMinutes = 15)
        => new(_repo, _users.Object, LockoutSettings(lockoutMinutes), _notifications.Object, _time);

    private static IConfigRepository LockoutSettings(int lockoutMinutes)
    {
        var config = SmbProtocolSettingsTransportTests.Config();
        config.Setup(c => c.GetIntAsync(LoginProtectionSettings.SmbLockoutMinutesKey, It.IsAny<int>())).ReturnsAsync(lockoutMinutes);
        return config.Object;
    }

    private SmbAuthenticationEvent Event(string status, string user = "Anna.Weber", string? address = "10.0.0.5", TimeSpan? ago = null)
        => new(Now - (ago ?? TimeSpan.Zero), user, address, status);

    private async Task<List<LoginAttemptRecord>> RowsAsync()
    {
        await using var db = NewContext();
        return await db.LoginAttempts.OrderBy(a => a.Id).ToListAsync();
    }

    [Fact]
    public async Task Failures_OfDifferentAccounts_AreSeparateSmbRows()
    {
        await Recorder().RecordAsync([Event("NT_STATUS_WRONG_PASSWORD"), Event("NT_STATUS_NO_SUCH_USER", user: "ghost")]);

        var rows = await RowsAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal(SecurityChannel.Smb, r.Channel);
            Assert.Equal(LoginOutcome.InvalidCredentials, r.Outcome);
            Assert.Equal("10.0.0.5", r.Address);
            Assert.Equal(1, r.RepeatCount);
        });
        Assert.Equal(["anna.weber", "ghost"], rows.Select(r => r.Username));
    }

    [Fact]
    public async Task Failures_OfOneAccountAddressAndHour_AreOneCountedRow()
    {
        var recorder = Recorder();
        await recorder.RecordAsync([Event("NT_STATUS_WRONG_PASSWORD"), Event("NT_STATUS_WRONG_PASSWORD")]);
        _time.Advance(TimeSpan.FromSeconds(30));
        await recorder.RecordAsync([Event("NT_STATUS_WRONG_PASSWORD")]);

        var row = Assert.Single(await RowsAsync());
        Assert.Equal(LoginOutcome.InvalidCredentials, row.Outcome);
        Assert.Equal(3, row.RepeatCount);

        var since = Now.AddDays(-1);
        Assert.Equal(3, (await _repo.GetTotalsAsync(since, Now)).Failures);
        Assert.Equal(3, Assert.Single(await _repo.SummarizeAccountsAsync(since, Now, 10)).Failures);
    }

    [Fact]
    public async Task Failures_FromAnotherAddressHourOrOutcome_StartNewRows()
    {
        var recorder = Recorder();
        await recorder.RecordAsync([
            Event("NT_STATUS_WRONG_PASSWORD"),
            Event("NT_STATUS_WRONG_PASSWORD", address: "10.0.0.6"),
            Event("NT_STATUS_ACCOUNT_DISABLED"),
        ]);
        _time.Advance(TimeSpan.FromHours(1));
        await recorder.RecordAsync([Event("NT_STATUS_WRONG_PASSWORD")]);

        Assert.Equal(4, (await RowsAsync()).Count);
    }

    // A spray of random user names (NT_STATUS_NO_SUCH_USER never locks anything) from one address.
    [Fact]
    public async Task FailureSpray_FromOneAddress_IsCappedAndCountedInOneAddressRow()
    {
        int cap = SmbAuthenticationRecorder.MaxFailureRowsPerAddressHour;
        var recorder = Recorder();
        await recorder.RecordAsync(Enumerable.Range(0, cap + 5)
            .Select(i => Event("NT_STATUS_NO_SUCH_USER", user: $"guess{i}")).ToList());
        await recorder.RecordAsync([Event("NT_STATUS_NO_SUCH_USER", user: "another")]);
        // Another address keeps its own budget.
        await recorder.RecordAsync([Event("NT_STATUS_NO_SUCH_USER", user: "another", address: "10.0.0.6")]);

        var rows = await RowsAsync();
        Assert.Equal(cap + 2, rows.Count);
        var overflow = Assert.Single(rows, r => r.Username.Length == 0);
        Assert.Equal("10.0.0.5", overflow.Address);
        Assert.Equal(LoginOutcome.InvalidCredentials, overflow.Outcome);
        Assert.Equal(6, overflow.RepeatCount);

        var since = Now.AddDays(-1);
        Assert.Equal(cap + 7, (await _repo.GetTotalsAsync(since, Now)).Failures);
        // The address row belongs to no account.
        Assert.DoesNotContain(await _repo.SummarizeAccountsAsync(since, Now, 1000), a => a.Username.Length == 0);
    }

    // Events without an address share one budget across batches as well.
    [Fact]
    public async Task FailureSpray_WithoutAddress_IsCappedAcrossBatches()
    {
        int cap = SmbAuthenticationRecorder.MaxFailureRowsPerAddressHour;
        var recorder = Recorder();
        await recorder.RecordAsync(Enumerable.Range(0, cap + 1)
            .Select(i => Event("NT_STATUS_NO_SUCH_USER", user: $"guess{i}", address: null)).ToList());
        await recorder.RecordAsync([Event("NT_STATUS_NO_SUCH_USER", user: "another", address: null)]);
        await recorder.RecordAsync([Event("NT_STATUS_NO_SUCH_USER", user: "third", address: null)]);

        var rows = await RowsAsync();
        Assert.Equal(cap + 1, rows.Count);
        var overflow = Assert.Single(rows, r => r.Username.Length == 0);
        Assert.Null(overflow.Address);
        Assert.Equal(3, overflow.RepeatCount);
    }

    [Fact]
    public async Task FailureCap_StillFoldsRepeatsOfAnAccountThatHasItsOwnRow()
    {
        int cap = SmbAuthenticationRecorder.MaxFailureRowsPerAddressHour;
        var recorder = Recorder();
        await recorder.RecordAsync(Enumerable.Range(0, cap)
            .Select(i => Event("NT_STATUS_WRONG_PASSWORD", user: i == 0 ? "anna.weber" : $"guess{i}")).ToList());
        await recorder.RecordAsync([Event("NT_STATUS_WRONG_PASSWORD")]);

        var rows = await RowsAsync();
        Assert.Equal(cap, rows.Count);
        Assert.Equal(2, Assert.Single(rows, r => r.Username == "anna.weber").RepeatCount);
    }

    [Fact]
    public async Task Successes_OfOneAccountAddressAndHour_AreOneCountedRow()
    {
        var recorder = Recorder();
        await recorder.RecordAsync([Event("NT_STATUS_OK"), Event("NT_STATUS_OK")]);
        _time.Advance(TimeSpan.FromSeconds(30));
        await recorder.RecordAsync([Event("NT_STATUS_OK")]);

        var row = Assert.Single(await RowsAsync());
        Assert.Equal(LoginOutcome.Success, row.Outcome);
        Assert.Equal(3, row.RepeatCount);

        var since = Now.AddDays(-1);
        Assert.Equal(3, (await _repo.GetTotalsAsync(since, Now)).Successes);
        Assert.Equal(3, Assert.Single(await _repo.SummarizeAccountsAsync(since, Now, 10)).Successes);
    }

    [Fact]
    public async Task Successes_FromAnotherAddressOrHour_StartNewRows()
    {
        var recorder = Recorder();
        await recorder.RecordAsync([Event("NT_STATUS_OK"), Event("NT_STATUS_OK", address: "10.0.0.6")]);
        _time.Advance(TimeSpan.FromHours(1));
        await recorder.RecordAsync([Event("NT_STATUS_OK")]);

        Assert.Equal(3, (await RowsAsync()).Count);
    }

    [Fact]
    public async Task LockedOutRepeats_FoldIntoTheRowOfTheirLock()
    {
        var recorder = Recorder(lockoutMinutes: 15);
        await recorder.RecordAsync([Event("NT_STATUS_ACCOUNT_LOCKED_OUT"), Event("NT_STATUS_ACCOUNT_LOCKED_OUT")]);
        var lockedAt = Now;
        _time.Advance(TimeSpan.FromMinutes(1));
        await recorder.RecordAsync([Event("NT_STATUS_ACCOUNT_LOCKED_OUT")]);

        var row = Assert.Single(await RowsAsync());
        Assert.Equal(LoginOutcome.LockedOut, row.Outcome);
        Assert.Equal(3, row.RepeatCount);
        Assert.Equal(lockedAt.AddMinutes(15), DateTime.SpecifyKind(row.LockedUntilUtc!.Value, DateTimeKind.Utc));
        Assert.Equal(1, (await _repo.GetTotalsAsync(Now.AddDays(-1), Now)).ActiveLockouts);
    }

    [Fact]
    public async Task LockedOut_AfterTheLockEnded_StartsANewRow()
    {
        var recorder = Recorder(lockoutMinutes: 15);
        await recorder.RecordAsync([Event("NT_STATUS_ACCOUNT_LOCKED_OUT")]);
        _time.Advance(TimeSpan.FromMinutes(16));
        await recorder.RecordAsync([Event("NT_STATUS_ACCOUNT_LOCKED_OUT")]);

        Assert.Equal(2, (await RowsAsync()).Count);
    }

    // A lock longer than the hour buckets still folds its repeats.
    [Fact]
    public async Task LockedOutRepeats_FoldIntoALockThatBeganHoursAgo()
    {
        var recorder = Recorder(lockoutMinutes: 180);
        await recorder.RecordAsync([Event("NT_STATUS_ACCOUNT_LOCKED_OUT")]);
        _time.Advance(TimeSpan.FromHours(2));
        await recorder.RecordAsync([Event("NT_STATUS_ACCOUNT_LOCKED_OUT")]);

        Assert.Equal(2, Assert.Single(await RowsAsync()).RepeatCount);
        Assert.Single(_published);
    }

    // An unlock from the user details ends the recorded lock; Samba's next lock is a new one.
    [Fact]
    public async Task LockedOut_AfterAnUnlock_StartsANewLockAndNotifiesAgain()
    {
        var recorder = Recorder(lockoutMinutes: 15);
        await recorder.RecordAsync([Event("NT_STATUS_ACCOUNT_LOCKED_OUT")]);
        _time.Advance(TimeSpan.FromMinutes(2));
        await _repo.EndActiveLocksAsync(SecurityChannel.Smb, "Anna.Weber", Now);
        var unlockedAt = Now;
        _time.Advance(TimeSpan.FromMinutes(3));
        await recorder.RecordAsync([Event("NT_STATUS_ACCOUNT_LOCKED_OUT")]);

        var rows = await RowsAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(unlockedAt, DateTime.SpecifyKind(rows[0].LockedUntilUtc!.Value, DateTimeKind.Utc));
        Assert.Equal(Now.AddMinutes(15), DateTime.SpecifyKind(rows[1].LockedUntilUtc!.Value, DateTimeKind.Utc));
        Assert.Equal(2, _published.Count);
        Assert.Equal(1, (await _repo.GetTotalsAsync(Now.AddDays(-1), Now)).ActiveLockouts);
    }

    // Samba applies an unlock with its next user sync; until then it still answers LockedOut
    // (Windows clients retry on their own). Those answers belong to the lifted lock.
    [Fact]
    public async Task LockedOut_SoonAfterAnUnlockRequest_StartsNoNewLockAndDoesNotNotify()
    {
        var recorder = Recorder(lockoutMinutes: 15);
        await recorder.RecordAsync([Event("NT_STATUS_ACCOUNT_LOCKED_OUT")]);
        _time.Advance(TimeSpan.FromMinutes(2));
        _anna.SmbUnlockRequestedAtUtc = Now;
        await _repo.EndActiveLocksAsync(SecurityChannel.Smb, "anna.weber", Now);
        _time.Advance(TimeSpan.FromSeconds(20));

        int recorded = await recorder.RecordAsync([Event("NT_STATUS_ACCOUNT_LOCKED_OUT"), Event("NT_STATUS_ACCOUNT_LOCKED_OUT", address: "10.0.0.6")]);

        Assert.Equal(0, recorded);
        Assert.Single(await RowsAsync());
        Assert.Single(_published);
        Assert.Equal(0, (await _repo.GetTotalsAsync(Now.AddDays(-1), Now)).ActiveLockouts);
    }

    [Fact]
    public async Task LockedOut_AfterTheUnlockGrace_IsANewLock()
    {
        var recorder = Recorder(lockoutMinutes: 15);
        await recorder.RecordAsync([Event("NT_STATUS_ACCOUNT_LOCKED_OUT")]);
        _anna.SmbUnlockRequestedAtUtc = Now;
        await _repo.EndActiveLocksAsync(SecurityChannel.Smb, "anna.weber", Now);
        _time.Advance(SmbAuthenticationRecorder.UnlockGrace + TimeSpan.FromSeconds(1));

        await recorder.RecordAsync([Event("NT_STATUS_ACCOUNT_LOCKED_OUT")]);

        Assert.Equal(2, (await RowsAsync()).Count);
        Assert.Equal(2, _published.Count);
        Assert.Equal(1, (await _repo.GetTotalsAsync(Now.AddDays(-1), Now)).ActiveLockouts);
    }

    // Samba lets a login through only while the account is not locked, so a success ends the
    // recorded lock (expired early, unlocked, password reset), from any address.
    [Fact]
    public async Task Success_EndsTheRunningLockOfThatAccount()
    {
        var recorder = Recorder(lockoutMinutes: 15);
        await recorder.RecordAsync([Event("NT_STATUS_ACCOUNT_LOCKED_OUT")]);
        _time.Advance(TimeSpan.FromMinutes(1));

        await recorder.RecordAsync([Event("NT_STATUS_OK", address: "10.0.0.6")]);

        var lockRow = (await RowsAsync())[0];
        Assert.Equal(Now, DateTime.SpecifyKind(lockRow.LockedUntilUtc!.Value, DateTimeKind.Utc));
        Assert.Equal(0, (await _repo.GetTotalsAsync(Now.AddDays(-1), Now)).ActiveLockouts);
    }

    [Fact]
    public async Task Success_InTheSameBatch_EndsOnlyTheLockBeforeIt()
    {
        var recorder = Recorder(lockoutMinutes: 15);
        await recorder.RecordAsync([
            Event("NT_STATUS_ACCOUNT_LOCKED_OUT", ago: TimeSpan.FromSeconds(30)),
            Event("NT_STATUS_OK", ago: TimeSpan.FromSeconds(20)),
            Event("NT_STATUS_ACCOUNT_LOCKED_OUT", ago: TimeSpan.FromSeconds(10)),
        ]);

        var locks = (await RowsAsync()).Where(r => r.Outcome == LoginOutcome.LockedOut).ToList();
        Assert.Equal(2, locks.Count);
        Assert.Equal(Now.AddSeconds(-20), DateTime.SpecifyKind(locks[0].LockedUntilUtc!.Value, DateTimeKind.Utc));
        Assert.Equal(Now.AddSeconds(-10).AddMinutes(15), DateTime.SpecifyKind(locks[1].LockedUntilUtc!.Value, DateTimeKind.Utc));
        Assert.Equal(1, (await _repo.GetTotalsAsync(Now.AddDays(-1), Now)).ActiveLockouts);
        Assert.Equal(2, _published.Count);
    }

    // A success must not end a lock recorded in an earlier batch that began after it.
    [Fact]
    public async Task Success_EndsAnEarlierBatchLockButNotTheNewLockOfItsOwnBatch()
    {
        var recorder = Recorder(lockoutMinutes: 15);
        await recorder.RecordAsync([Event("NT_STATUS_ACCOUNT_LOCKED_OUT", ago: TimeSpan.FromMinutes(2))]);

        await recorder.RecordAsync([
            Event("NT_STATUS_OK", ago: TimeSpan.FromSeconds(20)),
            Event("NT_STATUS_ACCOUNT_LOCKED_OUT", ago: TimeSpan.FromSeconds(10)),
        ]);

        var locks = (await RowsAsync()).Where(r => r.Outcome == LoginOutcome.LockedOut).ToList();
        Assert.Equal(2, locks.Count);
        Assert.Equal(Now.AddSeconds(-20), DateTime.SpecifyKind(locks[0].LockedUntilUtc!.Value, DateTimeKind.Utc));
        Assert.Equal(Now.AddSeconds(-10).AddMinutes(15), DateTime.SpecifyKind(locks[1].LockedUntilUtc!.Value, DateTimeKind.Utc));
    }

    [Fact]
    public async Task EndActiveLocks_EndsOnlyTheRunningLocksOfThatAccountAndChannel()
    {
        LoginAttemptRecord Lock(SecurityChannel channel, string user, DateTime until) => new()
        {
            AtUtc = Now.AddMinutes(-20), Channel = channel, Username = user, Address = "10.0.0.5",
            Outcome = LoginOutcome.LockedOut, LockedUntilUtc = until,
        };
        await _repo.SaveAsync([
            Lock(SecurityChannel.Smb, "anna.weber", Now.AddMinutes(10)),
            Lock(SecurityChannel.Smb, "anna.weber", Now.AddMinutes(-5)),
            Lock(SecurityChannel.Web, "anna.weber", Now.AddMinutes(10)),
            Lock(SecurityChannel.Smb, "admin", Now.AddMinutes(10)),
        ], []);

        await _repo.EndActiveLocksAsync(SecurityChannel.Smb, " Anna.Weber ", Now);

        var until = (await RowsAsync()).Select(r => DateTime.SpecifyKind(r.LockedUntilUtc!.Value, DateTimeKind.Utc)).ToList();
        Assert.Equal([Now, Now.AddMinutes(-5), Now.AddMinutes(10), Now.AddMinutes(10)], until);
    }

    // Counters only need the current hour; older rows load only while their lock still runs.
    [Fact]
    public async Task ListRecent_ReturnsOlderRowsOnlyForLocksStillRunning()
    {
        LoginAttemptRecord Row(LoginOutcome outcome, TimeSpan ago, DateTime? until = null) => new()
        {
            AtUtc = Now - ago, Channel = SecurityChannel.Smb, Username = "anna.weber", Address = "10.0.0.5",
            Outcome = outcome, LockedUntilUtc = until,
        };
        await _repo.SaveAsync([
            Row(LoginOutcome.InvalidCredentials, TimeSpan.FromHours(3)),
            Row(LoginOutcome.Success, TimeSpan.FromHours(3)),
            Row(LoginOutcome.LockedOut, TimeSpan.FromHours(3), Now.AddHours(-2)),
            Row(LoginOutcome.LockedOut, TimeSpan.FromHours(3), Now.AddHours(1)),
            Row(LoginOutcome.InvalidCredentials, TimeSpan.FromMinutes(10)),
        ], []);

        var rows = await _repo.ListRecentAsync(SecurityChannel.Smb, ["anna.weber"], ["10.0.0.5"], Now.AddHours(-1));

        Assert.Equal(2, rows.Count);
        Assert.Equal(LoginOutcome.LockedOut, rows[0].Outcome);
        Assert.Equal(LoginOutcome.InvalidCredentials, rows[1].Outcome);
    }

    [Fact]
    public async Task AccountLocked_IsPublishedOncePerAccountLock_EvenFromSeveralAddresses()
    {
        var recorder = Recorder(lockoutMinutes: 15);
        await recorder.RecordAsync([Event("NT_STATUS_ACCOUNT_LOCKED_OUT"), Event("NT_STATUS_ACCOUNT_LOCKED_OUT", address: "10.0.0.6")]);
        await recorder.RecordAsync([Event("NT_STATUS_ACCOUNT_LOCKED_OUT")]);

        var locked = Assert.Single(_published);
        Assert.Equal(NotificationEventCatalog.AccountLocked, locked.Type);
        Assert.Contains(_anna.Id.ToString(), locked.SubjectUserIdsJson);
        Assert.Contains("10.0.0.5", locked.PayloadJson);

        _time.Advance(TimeSpan.FromMinutes(16));
        await recorder.RecordAsync([Event("NT_STATUS_ACCOUNT_LOCKED_OUT")]);
        Assert.Equal(2, _published.Count);
    }

    // Samba answers LockedOut only for accounts in its passdb, so one for an unknown name can
    // only come from a forged log line: it must neither show a lock nor notify.
    [Fact]
    public async Task LockedOut_ForAnUnknownAccount_IsDropped()
    {
        int recorded = await Recorder().RecordAsync([Event("NT_STATUS_ACCOUNT_LOCKED_OUT", user: "ghost")]);

        Assert.Equal(0, recorded);
        Assert.Empty(await RowsAsync());
        Assert.Empty(_published);
    }

    [Fact]
    public async Task LockedOut_ResolvesTheAccountWhateverCaseTheClientSent()
    {
        await Recorder().RecordAsync([Event("NT_STATUS_ACCOUNT_LOCKED_OUT", user: "ANNA.WEBER")]);

        Assert.Equal("anna.weber", Assert.Single(await RowsAsync()).Username);
        Assert.Contains(_anna.Id.ToString(), Assert.Single(_published).SubjectUserIdsJson);
    }

    [Fact]
    public async Task UserRepository_FindsUsersCaseInsensitively()
    {
        var users = new UserRepository(DbFactory);
        await users.CreateAsync(new User(Guid.NewGuid(), "Bea", "Bea.Mixed", "pw", "nt"));

        Assert.Equal("Bea.Mixed", (await users.GetByUsernameIgnoreCaseAsync(" bea.mixed "))?.Username);
        Assert.Null(await users.GetByUsernameIgnoreCaseAsync("bea"));
    }

    [Theory]
    [InlineData("NT_STATUS_ACCOUNT_DISABLED", LoginOutcome.AccountDisabled)]
    [InlineData("NT_STATUS_LOGON_FAILURE", LoginOutcome.InvalidCredentials)]
    [InlineData("NT_STATUS_PASSWORD_MUST_CHANGE", LoginOutcome.InvalidCredentials)]
    public async Task Status_IsMappedToTheOutcome(string status, LoginOutcome expected)
    {
        await Recorder().RecordAsync([Event(status)]);

        Assert.Equal(expected, Assert.Single(await RowsAsync()).Outcome);
    }

    [Fact]
    public async Task UnusableEvents_AreSkippedOrClamped()
    {
        await Recorder().RecordAsync([
            Event("NT_STATUS_WRONG_PASSWORD", user: "  "),
            Event("NOT_A_STATUS"),
            Event("NT_STATUS_WRONG_PASSWORD", address: "not-an-ip"),
            Event("NT_STATUS_WRONG_PASSWORD", ago: TimeSpan.FromDays(-3)),
        ]);

        var rows = await RowsAsync();
        Assert.Equal(2, rows.Count);
        Assert.Null(rows[0].Address);
        // A time far in the future (wrong clock) is recorded as "now".
        Assert.Equal(Now, DateTime.SpecifyKind(rows[1].AtUtc, DateTimeKind.Utc));
    }

    [Fact]
    public async Task GrpcService_MapsTheEventsAndReportsTheAcceptedCount()
    {
        var request = new ReportAuthenticationRequest();
        request.Events.Add(new AuthenticationEvent
        {
            AtUnixMs = _time.Now.ToUnixTimeMilliseconds(), Username = "anna.weber", Address = "10.0.0.5",
            Status = "NT_STATUS_WRONG_PASSWORD",
        });
        var sut = new SecurityEventGrpcService(Recorder(), NullLogger<SecurityEventGrpcService>.Instance);

        var reply = await sut.ReportAuthentication(request, null!);

        Assert.Equal(1u, reply.Accepted);
        Assert.Equal(LoginOutcome.InvalidCredentials, Assert.Single(await RowsAsync()).Outcome);
    }

    [Fact]
    public async Task GrpcService_RejectsOversizedBatches()
    {
        var request = new ReportAuthenticationRequest();
        for (int i = 0; i <= SecurityEventGrpcService.MaxEventsPerCall; i++)
            request.Events.Add(new AuthenticationEvent { Username = "anna", Status = "NT_STATUS_OK" });
        var sut = new SecurityEventGrpcService(Recorder(), NullLogger<SecurityEventGrpcService>.Instance);

        var error = await Assert.ThrowsAsync<RpcException>(() => sut.ReportAuthentication(request, null!));

        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
        Assert.Empty(await RowsAsync());
    }

    [Fact]
    public void SambaIdentity_MayReportAuthentications()
        => Assert.True(ControlPlaneAccessPolicy.IsAllowed(
            ControlPlaneAccessPolicy.SambaClient,
            "/kaimo.smb.bridge.v1.SecurityEventService/ReportAuthentication"));

    [Fact]
    public void SmbChannel_IsAppendedSoStoredValuesKeepTheirMeaning()
    {
        Assert.Equal(0, (int)SecurityChannel.Web);
        Assert.Equal(1, (int)SecurityChannel.Api);
        Assert.Equal(2, (int)SecurityChannel.WebDav);
        Assert.Equal(3, (int)SecurityChannel.Smb);
    }
}
