using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Domain.Notifications;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Security.Authentication;
using Kaimo_File_Server.Core.Security.Credentials;
using Kaimo_File_Server.Core.Services.Notifications;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Web.Services;
using Kaimo_File_Server.Web.Services.SecondFactor;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Kaimo_File_Server.Infrastructure.Repositories.Identity;
using Kaimo_File_Server.Infrastructure.Services.Identity;

namespace Kaimo_File_Server.Web.Tests.Services;

/// <summary>
/// <see cref="SecondFactorService"/> over the real schema: enrollment, TOTP and recovery-code
/// verification (replay, single use), the account-wide lockout, disable/regenerate/reset, the
/// fail-closed behavior for an unreadable secret, and the notifications.
/// </summary>
public class SecondFactorServiceTests : DatabaseTestBase
{
    private readonly MutableTimeProvider _time = new() { Now = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero) };
    private readonly Mock<INotificationPublisher> _notifications = new();
    private readonly List<NotificationEvent> _published = new();
    private readonly IDataProtectionProvider _dataProtection = new EphemeralDataProtectionProvider();
    private readonly LoginThrottle _throttle;
    private readonly ConfigRepository _config;
    private readonly SecurityMonitor _monitor;

    public SecondFactorServiceTests()
    {
        _throttle = new LoginThrottle(_time);
        _monitor = new SecurityMonitor(timeProvider: _time);
        _config = new ConfigRepository(DbFactory, new MemoryCache(new MemoryCacheOptions()));
        _notifications.Setup(n => n.PublishAsync(It.IsAny<NotificationEvent>(), It.IsAny<CancellationToken>()))
            .Callback<NotificationEvent, CancellationToken>((e, _) => _published.Add(e))
            .Returns(Task.CompletedTask);
    }

    private SecondFactorPolicyService Policy() => new(
        _config, new UserContextFactory(DbFactory), DepartmentRepo(), ScopedRoleRepo(), RoleRepo(),
        new SecondFactorRepository(DbFactory), new MemoryCache(new MemoryCacheOptions()));

    private SecondFactorService Service(IDataProtectionProvider? dataProtection = null, SecondFactorPolicyService? policy = null) => new(
        new SecondFactorRepository(DbFactory), policy ?? Policy(), dataProtection ?? _dataProtection, _throttle,
        _config, _notifications.Object, _monitor, new SecurityEventRepository(DbFactory),
        NullLogger<SecondFactorService>.Instance, _time);

    private string CodeFor(byte[] secret, int stepOffset = 0)
        => Totp.Compute(secret, Totp.StepAt(_time.Now) + stepOffset);

    private async Task<(User User, byte[] Secret, IReadOnlyList<string> RecoveryCodes)> EnrolledUserAsync(string name = "erika")
    {
        var user = SeedUser(name);
        var enrollment = await Service().BeginEnrollmentAsync(user);
        var result = await Service().ConfirmEnrollmentAsync(user, enrollment.Secret, CodeFor(enrollment.Secret), "198.51.100.1");
        Assert.Equal(SecondFactorCheck.Success, result.Outcome);
        _time.Advance(TimeSpan.FromSeconds(Totp.StepSeconds));
        _published.Clear();
        return (user, enrollment.Secret, result.RecoveryCodes!);
    }

    private bool Published(string type, string? change = null)
        => _published.Any(e => e.Type == type && (change is null || e.PayloadJson.Contains($"\"{change}\"")));

    // ── Enrollment ──

    [Fact]
    public async Task BeginEnrollment_StoresNothingAndOffersQrAndManualKey()
    {
        var user = SeedUser("erika");
        var enrollment = await Service().BeginEnrollmentAsync(user);

        Assert.Equal(Totp.SecretBytes, enrollment.Secret.Length);
        Assert.StartsWith("data:image/png;base64,", enrollment.QrCodeDataUri);
        Assert.Equal(Base32.Encode(enrollment.Secret), enrollment.ManualKey.Replace(" ", ""));
        Assert.Null(await new SecondFactorRepository(DbFactory).GetAsync(user.Id));
    }

    [Fact]
    public async Task ConfirmEnrollment_WithValidCode_StoresFactorAndReturnsTenRecoveryCodes()
    {
        var user = SeedUser("erika");
        var enrollment = await Service().BeginEnrollmentAsync(user);

        var result = await Service().ConfirmEnrollmentAsync(user, enrollment.Secret, CodeFor(enrollment.Secret), null);

        Assert.Equal(SecondFactorCheck.Success, result.Outcome);
        Assert.Equal(10, result.RecoveryCodes!.Count);
        Assert.All(result.RecoveryCodes, c => Assert.Matches("^[A-Z2-7]{4}-[A-Z2-7]{4}-[A-Z2-7]{4}-[A-Z2-7]{4}$", c));
        Assert.Equal(10, result.RecoveryCodes.Distinct().Count());
        var stored = await new SecondFactorRepository(DbFactory).GetAsync(user.Id);
        Assert.NotNull(stored!.ConfirmedAtUtc);
        // The secret is stored encrypted, never as the plain base32 key.
        Assert.DoesNotContain(Base32.Encode(enrollment.Secret), stored.SecretProtected);
        Assert.True(Published(NotificationEventCatalog.SecondFactorChanged, "enabled"));
    }

    [Fact]
    public async Task ConfirmEnrollment_WithWrongCode_StoresNothing()
    {
        var user = SeedUser("erika");
        var enrollment = await Service().BeginEnrollmentAsync(user);

        var result = await Service().ConfirmEnrollmentAsync(user, enrollment.Secret, "000000", null);

        Assert.Equal(SecondFactorCheck.Invalid, result.Outcome);
        Assert.Null(await new SecondFactorRepository(DbFactory).GetAsync(user.Id));
    }

    [Fact]
    public async Task ConfirmEnrollment_WhenAlreadyEnrolled_IsUnavailable()
    {
        var (user, _, _) = await EnrolledUserAsync();
        var second = await Service().BeginEnrollmentAsync(user);

        var result = await Service().ConfirmEnrollmentAsync(user, second.Secret, CodeFor(second.Secret), null);

        Assert.Equal(SecondFactorCheck.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task ConfirmEnrollment_WhenAlreadyEnrolled_DoesNotResetTheFailureBudget()
    {
        var (user, _, _) = await EnrolledUserAsync();
        int max = (await _config.LoadLoginProtectionAsync()).MaxAttempts;
        for (int i = 0; i < max - 1; i++)
            await Service().VerifyAsync(user, "000000", null);

        // A valid code for a fresh secret must not wipe the failed guesses against the real one.
        var second = await Service().BeginEnrollmentAsync(user);
        await Service().ConfirmEnrollmentAsync(user, second.Secret, CodeFor(second.Secret), null);

        Assert.Equal(SecondFactorCheck.LockedOut, (await Service().VerifyAsync(user, "000000", null)).Outcome);
    }

    // ── Verification ──

    [Fact]
    public async Task Verify_ValidCode_Succeeds_AndTheSameCodeCannotBeReplayed()
    {
        var (user, secret, _) = await EnrolledUserAsync();
        string code = CodeFor(secret);

        Assert.Equal(SecondFactorCheck.Success, (await Service().VerifyAsync(user, code, null)).Outcome);
        Assert.Equal(SecondFactorCheck.Invalid, (await Service().VerifyAsync(user, code, null)).Outcome);
    }

    [Fact]
    public async Task Verify_WrongCode_IsInvalid_RecordedAndNotified()
    {
        var (user, _, _) = await EnrolledUserAsync();

        var result = await Service().VerifyAsync(user, "000000", "203.0.113.7");

        Assert.Equal(SecondFactorCheck.Invalid, result.Outcome);
        Assert.True(Published(NotificationEventCatalog.SecondFactorFailed));
    }

    [Fact]
    public async Task Verify_LocksTheAccountAfterTheConfiguredFailures_EvenWithTheRightCodeAfterwards()
    {
        var (user, secret, _) = await EnrolledUserAsync();
        int max = (await _config.LoadLoginProtectionAsync()).MaxAttempts;

        SecondFactorCheckResult last = null!;
        for (int i = 0; i < max; i++)
            last = await Service().VerifyAsync(user, "000000", $"203.0.113.{i}");

        Assert.Equal(SecondFactorCheck.LockedOut, last.Outcome);
        Assert.True(last.RetryAfter > TimeSpan.Zero);
        // The lock is per account, not per address: a new address does not get new tries.
        Assert.Equal(SecondFactorCheck.LockedOut, (await Service().VerifyAsync(user, CodeFor(secret), "198.51.100.9")).Outcome);
    }

    [Fact]
    public async Task Verify_SuccessResetsTheFailureCount()
    {
        var (user, secret, _) = await EnrolledUserAsync();
        int max = (await _config.LoadLoginProtectionAsync()).MaxAttempts;
        for (int i = 0; i < max - 1; i++)
            await Service().VerifyAsync(user, "000000", null);

        Assert.Equal(SecondFactorCheck.Success, (await Service().VerifyAsync(user, CodeFor(secret), null)).Outcome);
        Assert.Equal(SecondFactorCheck.Invalid, (await Service().VerifyAsync(user, "000000", null)).Outcome);
    }

    [Fact]
    public async Task Verify_RecoveryCode_WorksOnce_InAnyFormatting()
    {
        var (user, _, codes) = await EnrolledUserAsync();
        string formatted = codes[0].ToLowerInvariant().Replace("-", " ");

        var first = await Service().VerifyAsync(user, formatted, null);
        var second = await Service().VerifyAsync(user, codes[0], null);

        Assert.Equal(SecondFactorCheck.RecoveryCodeUsed, first.Outcome);
        Assert.Equal(codes.Count - 1, first.RecoveryCodesLeft);
        Assert.Equal(SecondFactorCheck.Invalid, second.Outcome);
        Assert.True(Published(NotificationEventCatalog.SecondFactorChanged, "recovery_code_used"));
    }

    [Fact]
    public async Task Verify_WithoutFactor_IsUnavailable()
    {
        Assert.Equal(SecondFactorCheck.Unavailable, (await Service().VerifyAsync(SeedUser("max"), "123456", null)).Outcome);
    }

    [Fact]
    public async Task Verify_UnreadableSecret_FailsClosed()
    {
        var (user, secret, _) = await EnrolledUserAsync();
        // A different key ring, as after a restore without the Data Protection keys.
        var otherKeys = new EphemeralDataProtectionProvider();

        var result = await Service(otherKeys).VerifyAsync(user, CodeFor(secret), null);

        Assert.Equal(SecondFactorCheck.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task Secret_IsBoundToItsUser()
    {
        var (erika, erikaSecret, _) = await EnrolledUserAsync("erika");
        var (max, _, _) = await EnrolledUserAsync("max");
        // Copy Erika's encrypted secret onto Max's row: it must not decrypt for Max.
        await using (var db = NewContext())
        {
            var erikaRow = db.UserSecondFactors.Single(f => f.UserId == erika.Id);
            db.UserSecondFactors.Single(f => f.UserId == max.Id).SecretProtected = erikaRow.SecretProtected;
            await db.SaveChangesAsync();
        }

        Assert.Equal(SecondFactorCheck.Unavailable, (await Service().VerifyAsync(max, CodeFor(erikaSecret), null)).Outcome);
    }

    // ── Self-service and admin ──

    [Fact]
    public async Task Disable_WithValidCode_RemovesFactor_WhenNotRequired()
    {
        var (user, secret, _) = await EnrolledUserAsync();

        var result = await Service().DisableAsync(ContextFor(user), CodeFor(secret), null);

        Assert.Equal(SecondFactorCheck.Success, result.Outcome);
        Assert.Null(await new SecondFactorRepository(DbFactory).GetAsync(user.Id));
        Assert.True(Published(NotificationEventCatalog.SecondFactorChanged, "disabled"));
    }

    [Fact]
    public async Task Disable_WhenThePolicyRequiresIt_IsUnavailable()
    {
        var (user, secret, _) = await EnrolledUserAsync();
        await Policy().SavePolicyAsync(new SecondFactorPolicy { Mode = SecondFactorMode.Required, AppliesToAll = true });

        var result = await Service().DisableAsync(ContextFor(user), CodeFor(secret), null);

        Assert.Equal(SecondFactorCheck.Unavailable, result.Outcome);
        Assert.NotNull(await new SecondFactorRepository(DbFactory).GetAsync(user.Id));
    }

    [Fact]
    public async Task Disable_WithRecoveryCode_IsRefused()
    {
        var (user, _, codes) = await EnrolledUserAsync();
        Assert.Equal(SecondFactorCheck.Invalid, (await Service().DisableAsync(ContextFor(user), codes[0], null)).Outcome);
    }

    [Fact]
    public async Task RegenerateRecoveryCodes_ReplacesAllCodes()
    {
        var (user, secret, oldCodes) = await EnrolledUserAsync();

        var result = await Service().RegenerateRecoveryCodesAsync(user, CodeFor(secret), null);

        Assert.Equal(SecondFactorCheck.Success, result.Outcome);
        Assert.Equal(10, result.RecoveryCodes!.Count);
        Assert.Empty(result.RecoveryCodes.Intersect(oldCodes));
        _time.Advance(TimeSpan.FromSeconds(Totp.StepSeconds));
        Assert.Equal(SecondFactorCheck.Invalid, (await Service().VerifyAsync(user, oldCodes[1], null)).Outcome);
        Assert.Equal(SecondFactorCheck.RecoveryCodeUsed, (await Service().VerifyAsync(user, result.RecoveryCodes[0], null)).Outcome);
    }

    [Fact]
    public async Task Reset_RemovesFactorAndNotifiesWithActor()
    {
        var (user, _, _) = await EnrolledUserAsync();
        var admin = SeedUser("admin");

        Assert.True(await Service().ResetAsync(user, admin.Id));

        Assert.Null(await new SecondFactorRepository(DbFactory).GetAsync(user.Id));
        Assert.Contains(_published, e => e.Type == NotificationEventCatalog.SecondFactorChanged
                                         && e.PayloadJson.Contains("\"reset\"") && e.ActorUserId == admin.Id);
        Assert.False(await Service().ResetAsync(user, admin.Id));
    }

    [Fact]
    public async Task Reset_WhenTheFactorIsAlreadyGone_StillDropsTheCachedSessionResult()
    {
        // E.g. a retried delete whose first attempt committed: nothing left to delete, but the
        // cached "needs a second factor" must not outlive the factor.
        var (user, _, _) = await EnrolledUserAsync();
        var policy = Policy();
        Assert.True(await policy.SessionRequiresSecondFactorAsync(user.Id));
        await new SecondFactorRepository(DbFactory).DeleteAsync(user.Id);

        Assert.False(await Service(policy: policy).ResetAsync(user, SeedUser("admin").Id));

        Assert.False(await policy.SessionRequiresSecondFactorAsync(user.Id));
        Assert.DoesNotContain(_published, e => e.Type == NotificationEventCatalog.SecondFactorChanged);
    }

    private async Task LockSecondFactorAsync(User user)
    {
        int max = (await _config.LoadLoginProtectionAsync()).MaxAttempts;
        for (int i = 0; i <= max; i++)
            await Service().VerifyAsync(user, "000000", "203.0.113.7");
    }

    private async Task<List<LoginAttemptRecord>> LockRowsAsync(LoginOutcome outcome)
    {
        await using var db = NewContext();
        return await db.LoginAttempts.Where(a => a.Outcome == outcome).ToListAsync();
    }

    [Fact]
    public async Task Verify_Lockout_IsLoggedAsSecondFactorLockout_NotAsPasswordLockout()
    {
        var (user, _, _) = await EnrolledUserAsync();

        await LockSecondFactorAsync(user);
        await _monitor.FlushAsync(new SecurityEventRepository(DbFactory));

        var row = Assert.Single(await LockRowsAsync(LoginOutcome.SecondFactorLockedOut));
        Assert.True(row.LockedUntilUtc > _time.Now.UtcDateTime);
        Assert.Empty(await LockRowsAsync(LoginOutcome.LockedOut));
    }

    [Fact]
    public async Task Reset_EndsTheSecondFactorLockout_InTheSecurityLog()
    {
        var (user, _, _) = await EnrolledUserAsync();
        await LockSecondFactorAsync(user); // still unflushed: the reset has to write it first

        await Service().ResetAsync(user, SeedUser("admin").Id);

        var now = _time.Now.UtcDateTime;
        var events = new SecurityEventRepository(DbFactory);
        Assert.Equal(0, (await events.GetTotalsAsync(now.AddDays(-1), now)).ActiveLockouts);
        Assert.Null(Assert.Single(await events.SummarizeAccountsAsync(now.AddDays(-1), now, 10)).LockedUntilUtc);
    }

    [Fact]
    public async Task Reset_KeepsAPasswordLockout_InTheSecurityLog()
    {
        // The reset lifts the second-factor budget only; a running password lock stays.
        var (user, _, _) = await EnrolledUserAsync();
        await LockSecondFactorAsync(user);
        _monitor.RecordLogin(SecurityChannel.Web, user.Username, "203.0.113.7", LoginResult.LockedOut(TimeSpan.FromMinutes(15)));

        await Service().ResetAsync(user, SeedUser("admin").Id);
        await _monitor.FlushAsync(new SecurityEventRepository(DbFactory));

        var now = _time.Now.UtcDateTime;
        Assert.True(Assert.Single(await LockRowsAsync(LoginOutcome.LockedOut)).LockedUntilUtc > now);
        Assert.All(await LockRowsAsync(LoginOutcome.SecondFactorLockedOut), r => Assert.True(r.LockedUntilUtc <= now));
    }

    [Fact]
    public async Task Reset_RotatesTheSecurityStamp_SoExistingSessionsEnd()
    {
        var (user, _, _) = await EnrolledUserAsync();
        var admin = SeedUser("admin");

        await Service().ResetAsync(user, admin.Id);

        Assert.NotEqual(user.SecurityStamp, (await UserRepo().GetByIdAsync(user.Id))!.SecurityStamp);
    }

    [Fact]
    public async Task Disable_KeepsTheSecurityStamp()
    {
        var (user, secret, _) = await EnrolledUserAsync();

        Assert.Equal(SecondFactorCheck.Success, (await Service().DisableAsync(ContextFor(user), CodeFor(secret), null)).Outcome);

        Assert.Equal(user.SecurityStamp, (await UserRepo().GetByIdAsync(user.Id))!.SecurityStamp);
    }

    [Fact]
    public async Task GetStatus_ReportsEnrollmentCodesAndRequirement()
    {
        var (user, _, _) = await EnrolledUserAsync();
        await Policy().SavePolicyAsync(new SecondFactorPolicy { Mode = SecondFactorMode.Required, UserIds = [user.Id], RequireForManagementRoles = false });

        var status = await Service().GetStatusAsync(ContextFor(user));
        var other = await Service().GetStatusAsync(ContextFor(SeedUser("max")));

        Assert.True(status.IsEnrolled);
        Assert.True(status.IsRequired);
        Assert.Equal(10, status.RecoveryCodesLeft);
        Assert.False(other.IsEnrolled);
        Assert.False(other.IsRequired);
        Assert.True(other.IsAvailable);
    }
}
