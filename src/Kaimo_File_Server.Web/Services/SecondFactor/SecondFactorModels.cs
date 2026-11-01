namespace Kaimo_File_Server.Web.Services.SecondFactor;

/// <summary>
/// A not yet confirmed TOTP enrollment. Lives only in the circuit until the user enters the
/// first valid code; nothing is stored before that.
/// </summary>
/// <param name="Secret">The new secret; never logged or rendered except as <paramref name="ManualKey"/> and in the QR code.</param>
/// <param name="ManualKey">The secret in base32, grouped in fours, for typing it into the app.</param>
/// <param name="QrCodeDataUri">PNG data URI of the <c>otpauth://</c> provisioning URI.</param>
public sealed record SecondFactorEnrollment(byte[] Secret, string ManualKey, string QrCodeDataUri);

public enum SecondFactorCheck
{
    Success,

    /// <summary>A recovery code was accepted (and is used up now).</summary>
    RecoveryCodeUsed,

    Invalid,
    LockedOut,

    /// <summary>
    /// Not possible for this account: no confirmed factor, the policy forbids the action, or the
    /// stored secret cannot be decrypted (missing Data Protection key ring). Fails closed.
    /// </summary>
    Unavailable,
}

/// <param name="RecoveryCodes">New recovery codes, shown once (after enrollment or regeneration).</param>
public sealed record SecondFactorCheckResult(
    SecondFactorCheck Outcome,
    TimeSpan RetryAfter = default,
    int RecoveryCodesLeft = 0,
    IReadOnlyList<string>? RecoveryCodes = null)
{
    public bool Succeeded => Outcome is SecondFactorCheck.Success or SecondFactorCheck.RecoveryCodeUsed;
}

/// <summary>The second-factor state of one account, for the profile and the admin panel.</summary>
public sealed record SecondFactorStatus(
    bool IsAvailable,
    bool IsEnrolled,
    DateTime? ConfirmedAtUtc,
    int RecoveryCodesLeft,
    bool IsRequired);
