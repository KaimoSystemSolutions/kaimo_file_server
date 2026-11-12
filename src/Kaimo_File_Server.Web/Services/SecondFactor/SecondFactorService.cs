using System.Security.Cryptography;
using System.Text;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories.Identity;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Security.Authentication;
using Kaimo_File_Server.Core.Security.Credentials;
using Kaimo_File_Server.Core.Services.Notifications;
using Kaimo_File_Server.Infrastructure.Configuration;
using Microsoft.AspNetCore.DataProtection;
using QRCoder;
using Kaimo_File_Server.Infrastructure.Services.Identity;

namespace Kaimo_File_Server.Web.Services.SecondFactor;

/// <summary>
/// The TOTP second factor of the web login: enrollment, verification of codes and recovery
/// codes, self-service removal, recovery-code regeneration and the administrator reset.
///
/// Every code check of one account shares one account-wide budget (<see cref="ThrottleKey"/>,
/// sized like the per-user login budget): whoever reaches this step already knows the password,
/// so a per-address budget would not slow them down. Secrets are encrypted with Data Protection
/// and bound to their user id; a secret that cannot be decrypted (key ring lost) fails closed.
/// Failed codes are recorded in the security log and raise a notification, because they mean
/// the password is known to someone else.
/// </summary>
public sealed class SecondFactorService(
    ISecondFactorRepository factors,
    SecondFactorPolicyService policy,
    IDataProtectionProvider dataProtection,
    ILoginThrottle throttle,
    IConfigRepository config,
    INotificationPublisher notifications,
    SecurityMonitor monitor,
    ISecurityEventRepository securityEvents,
    ILogger<SecondFactorService> logger,
    TimeProvider? timeProvider = null)
{
    public const int RecoveryCodeCount = 10;

    // 10 random bytes are exactly 16 base32 characters (80 bits).
    private const int RecoveryCodeBytes = 10;
    private const int RecoveryCodeLength = 16;
    private const string ProtectorPurpose = "Kaimo.SecondFactor.Totp";

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Throttle key of the code checks of one account.</summary>
    // Leading "|" cannot start a user name, so this never collides with a login key.
    internal static string ThrottleKey(Guid userId) => "|mfa|" + userId.ToString("N");

    public async Task<SecondFactorStatus> GetStatusAsync(UserContext user)
    {
        var factor = await factors.GetAsync(user.User.Id);
        bool enrolled = factor?.ConfirmedAtUtc is not null;
        return new SecondFactorStatus(
            await policy.IsAvailableAsync(),
            enrolled,
            factor?.ConfirmedAtUtc,
            enrolled ? await factors.CountUnusedRecoveryCodesAsync(user.User.Id) : 0,
            await policy.IsRequiredAsync(user));
    }

    /// <summary>A new secret with its QR code. Nothing is stored until <see cref="ConfirmEnrollmentAsync"/>.</summary>
    public async Task<SecondFactorEnrollment> BeginEnrollmentAsync(User user)
    {
        var secret = Totp.NewSecret();
        var uri = Totp.ProvisioningUri(secret, (await policy.GetPolicyAsync()).EffectiveIssuer, user.Username);
        using var data = QRCodeGenerator.GenerateQrCode(uri, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(6);
        return new SecondFactorEnrollment(secret, Group(Base32.Encode(secret)), "data:image/png;base64," + Convert.ToBase64String(png));
    }

    /// <summary>
    /// Stores the secret once the user proved the app works with a valid code, and returns the
    /// recovery codes (shown once). Unavailable when the account already has a confirmed factor.
    /// </summary>
    public async Task<SecondFactorCheckResult> ConfirmEnrollmentAsync(User user, byte[] secret, string code, string? remoteAddress)
    {
        var (gate, budget) = await BeginAsync(user.Id);
        if (gate is not null) return gate;

        var step = Totp.Verify(secret, code, _time.GetUtcNow(), lastUsedStep: 0);
        if (step is null)
            return Fail(user, remoteAddress, budget, record: false);

        var codes = NewRecoveryCodes();
        bool confirmed = false;
        try
        {
            confirmed = await factors.ConfirmAsync(user.Id, Protector(user.Id).Protect(Convert.ToBase64String(secret)),
                step.Value, codes.Select(HashRecoveryCode).ToList(), _time.GetUtcNow().UtcDateTime);
        }
        finally
        {
            // Only a stored factor clears the budget: a code for a fresh secret must not wipe the
            // failed guesses against an existing one.
            if (confirmed)
                throttle.Reset(ThrottleKey(user.Id));
            else
                throttle.EndAttempt(ThrottleKey(user.Id));
        }
        if (!confirmed)
            return new SecondFactorCheckResult(SecondFactorCheck.Unavailable);

        policy.Invalidate(user.Id);
        logger.LogInformation("Second factor set up for user {UserId}", user.Id);
        await NotifyChangedAsync(user, "enabled", user.Id);
        return new SecondFactorCheckResult(SecondFactorCheck.Success, RecoveryCodesLeft: codes.Count, RecoveryCodes: codes);
    }

    /// <summary>The login step: a TOTP code or a recovery code (which is used up).</summary>
    public Task<SecondFactorCheckResult> VerifyAsync(User user, string code, string? remoteAddress)
        => VerifyCoreAsync(user, code, remoteAddress, allowRecoveryCode: true);

    /// <summary>Removes the own factor after a valid TOTP code; refused while the policy requires one.</summary>
    public async Task<SecondFactorCheckResult> DisableAsync(UserContext user, string code, string? remoteAddress)
    {
        if (await policy.IsRequiredAsync(user))
            return new SecondFactorCheckResult(SecondFactorCheck.Unavailable);

        var check = await VerifyCoreAsync(user.User, code, remoteAddress, allowRecoveryCode: false);
        if (check.Outcome != SecondFactorCheck.Success)
            return check;

        await factors.DeleteAsync(user.User.Id);
        policy.Invalidate(user.User.Id);
        logger.LogInformation("Second factor removed by user {UserId}", user.User.Id);
        await NotifyChangedAsync(user.User, "disabled", user.User.Id);
        return check;
    }

    /// <summary>Replaces all recovery codes after a valid TOTP code and returns the new ones.</summary>
    public async Task<SecondFactorCheckResult> RegenerateRecoveryCodesAsync(User user, string code, string? remoteAddress)
    {
        var check = await VerifyCoreAsync(user, code, remoteAddress, allowRecoveryCode: false);
        if (check.Outcome != SecondFactorCheck.Success)
            return check;

        var codes = NewRecoveryCodes();
        await factors.ReplaceRecoveryCodesAsync(user.Id, codes.Select(HashRecoveryCode).ToList());
        logger.LogInformation("Recovery codes regenerated for user {UserId}", user.Id);
        await NotifyChangedAsync(user, "recovery_codes_regenerated", user.Id);
        return check with { RecoveryCodesLeft = codes.Count, RecoveryCodes = codes };
    }

    /// <summary>
    /// Administrator reset: removes the factor and the recovery codes and rotates the security
    /// stamp, so every existing session of the account ends — including one of an attacker who
    /// set up their own authenticator. The caller checks the permission. The next web login asks
    /// for a new set-up when the policy requires one.
    /// </summary>
    public async Task<bool> ResetAsync(User user, Guid actorId)
    {
        bool removed = await factors.DeleteAsync(user.Id, rotateSecurityStamp: true);
        // The factor is gone either way, so nothing cached about it may outlive it.
        // ponytail: a retried delete whose first attempt committed also reports false, and its
        // notification is lost; a reset id on the factor row would tell the two apart.
        throttle.Reset(ThrottleKey(user.Id));
        policy.Invalidate(user.Id);
        await EndCodeLockInSecurityLogAsync(user);
        if (!removed)
            return false;
        logger.LogWarning("Second factor of user {UserId} reset by {ActorId}", user.Id, actorId);
        await NotifyChangedAsync(user, "reset", actorId);
        return true;
    }

    private async Task<SecondFactorCheckResult> VerifyCoreAsync(User user, string code, string? remoteAddress, bool allowRecoveryCode)
    {
        var factor = await factors.GetAsync(user.Id);
        if (factor?.ConfirmedAtUtc is null)
            return new SecondFactorCheckResult(SecondFactorCheck.Unavailable);

        var (gate, budget) = await BeginAsync(user.Id);
        if (gate is not null)
        {
            monitor.RecordLogin(SecurityChannel.Web, user.Username, remoteAddress, LockedOut(gate.RetryAfter));
            return gate;
        }

        bool settled = false;
        try
        {
            if (NormalizeRecoveryCode(code) is { } recoveryCode)
            {
                if (allowRecoveryCode && await factors.TryUseRecoveryCodeAsync(user.Id, HashRecoveryCode(recoveryCode), _time.GetUtcNow().UtcDateTime))
                {
                    settled = true;
                    throttle.Reset(ThrottleKey(user.Id));
                    logger.LogWarning("User {UserId} signed in with a recovery code", user.Id);
                    await NotifyChangedAsync(user, "recovery_code_used", user.Id);
                    return new SecondFactorCheckResult(SecondFactorCheck.RecoveryCodeUsed,
                        RecoveryCodesLeft: await factors.CountUnusedRecoveryCodesAsync(user.Id));
                }
            }
            else if (Unprotect(user.Id, factor.SecretProtected) is not { } secret)
            {
                // Not the user's fault and not a guess: neither counted nor recorded as one.
                return new SecondFactorCheckResult(SecondFactorCheck.Unavailable);
            }
            else if (Totp.Verify(secret, code, _time.GetUtcNow(), factor.LastUsedStep) is { } step
                     && await factors.TryAdvanceStepAsync(user.Id, step))
            {
                settled = true;
                throttle.Reset(ThrottleKey(user.Id));
                return new SecondFactorCheckResult(SecondFactorCheck.Success,
                    RecoveryCodesLeft: await factors.CountUnusedRecoveryCodesAsync(user.Id));
            }

            settled = true;
            var failure = Fail(user, remoteAddress, budget, record: true);
            await NotifyFailedAsync(user, remoteAddress);
            return failure;
        }
        finally
        {
            // Infrastructure errors and an unreadable secret do not shrink the budget.
            if (!settled)
                throttle.EndAttempt(ThrottleKey(user.Id));
        }
    }

    /// <summary>Reserves one code check; a non-null result means the account is locked.</summary>
    private async Task<(SecondFactorCheckResult? Locked, LoginThrottlePolicy Budget)> BeginAsync(Guid userId)
    {
        var budget = (await config.LoadLoginProtectionAsync()).UserPolicy;
        var status = throttle.BeginAttempt(ThrottleKey(userId), budget);
        return status.IsLockedOut
            ? (new SecondFactorCheckResult(SecondFactorCheck.LockedOut, status.RetryAfter), budget)
            : (null, budget);
    }

    private SecondFactorCheckResult Fail(User user, string? remoteAddress, LoginThrottlePolicy budget, bool record)
    {
        var status = throttle.RegisterFailure(ThrottleKey(user.Id), budget);
        if (record)
            monitor.RecordLogin(SecurityChannel.Web, user.Username, remoteAddress, status.IsLockedOut
                ? LockedOut(status.RetryAfter)
                : new LoginResult(LoginOutcome.SecondFactorFailed));
        return status.IsLockedOut
            ? new SecondFactorCheckResult(SecondFactorCheck.LockedOut, status.RetryAfter)
            : new SecondFactorCheckResult(SecondFactorCheck.Invalid);
    }

    /// <summary>
    /// The lifted code lock must not stay "locked until" in the security overview. Flushes first,
    /// as its entry may still be queued in the monitor; a password lock is a separate entry and
    /// stays. Only the overview depends on this, so a failure does not fail the reset.
    /// </summary>
    private async Task EndCodeLockInSecurityLogAsync(User user)
    {
        try
        {
            await monitor.FlushAsync(securityEvents);
            await securityEvents.EndActiveLocksAsync(SecurityChannel.Web, user.Username, _time.GetUtcNow().UtcDateTime,
                LoginOutcome.SecondFactorLockedOut);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not end the second-factor lock of user {UserId} in the security log", user.Id);
        }
    }

    /// <summary>The security-log entry of a locked code budget, apart from a password lock (see <see cref="ResetAsync"/>).</summary>
    private static LoginResult LockedOut(TimeSpan retryAfter)
        => new(LoginOutcome.SecondFactorLockedOut, RetryAfter: retryAfter);

    private IDataProtector Protector(Guid userId)
        => dataProtection.CreateProtector(ProtectorPurpose, "v1", userId.ToString("N"));

    private byte[]? Unprotect(Guid userId, string protectedSecret)
    {
        try
        {
            return Convert.FromBase64String(Protector(userId).Unprotect(protectedSecret));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            logger.LogError(
                "The second factor of user {UserId} cannot be decrypted (Data Protection key ring missing or changed?). " +
                "An administrator has to reset it.", userId);
            return null;
        }
    }

    private static List<string> NewRecoveryCodes()
        => Enumerable.Range(0, RecoveryCodeCount)
            .Select(_ => Group(Base32.Encode(RandomNumberGenerator.GetBytes(RecoveryCodeBytes)), "-"))
            .ToList();

    /// <summary>The code in upper case without separators, or <c>null</c> unless it looks like a recovery code.</summary>
    private static string? NormalizeRecoveryCode(string? code)
    {
        var normalized = (code ?? "").Replace(" ", "").Replace("-", "").ToUpperInvariant();
        return normalized.Length == RecoveryCodeLength && normalized.All(c => c is >= 'A' and <= 'Z' or >= '2' and <= '7')
            ? normalized
            : null;
    }

    private static string HashRecoveryCode(string code)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(NormalizeRecoveryCode(code) ?? code)));

    private static string Group(string value, string separator = " ")
        => string.Join(separator, value.Chunk(4).Select(c => new string(c)));

    private Task NotifyChangedAsync(User user, string change, Guid actorId)
        => notifications.PublishAsync(NotificationEvents.SecondFactorChanged(user.Id, user.Name, user.Username, change, actorId));

    private Task NotifyFailedAsync(User user, string? remoteAddress)
        => notifications.PublishAsync(NotificationEvents.SecondFactorFailed(user.Id, user.Username, remoteAddress));
}
