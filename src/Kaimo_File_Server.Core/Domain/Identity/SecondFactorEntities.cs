namespace Kaimo_File_Server.Core.Domain.Identity;

/// <summary>
/// A user's TOTP second factor for the web login. Kept apart from <see cref="User"/> so the
/// secret never travels with the user entity, which is loaded and cached all over the app.
/// A row with <see cref="ConfirmedAtUtc"/> = <c>null</c> is an enrollment that was started but
/// not verified yet and counts as "not set up".
/// </summary>
public sealed class UserSecondFactor
{
    public Guid UserId { get; set; }

    /// <summary>The 160-bit TOTP secret, encrypted with ASP.NET Core Data Protection.</summary>
    public string SecretProtected { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>When the user entered the first valid code; <c>null</c> while enrollment is pending.</summary>
    public DateTime? ConfirmedAtUtc { get; set; }

    /// <summary>
    /// Highest TOTP time step accepted so far. A code is only accepted for a later step,
    /// so a code cannot be used twice within its validity window.
    /// </summary>
    public long LastUsedStep { get; set; }
}

/// <summary>A single-use recovery code; only its SHA-256 hash is stored.</summary>
public sealed class UserRecoveryCode
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    /// <summary>Hex SHA-256 of the normalized code (high entropy, so a slow hash is not needed).</summary>
    public string CodeHash { get; set; } = string.Empty;

    public DateTime? UsedAtUtc { get; set; }
}
