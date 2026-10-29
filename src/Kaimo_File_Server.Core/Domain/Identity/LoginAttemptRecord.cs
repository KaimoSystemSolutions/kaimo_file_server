using Kaimo_File_Server.Core.Security;

namespace Kaimo_File_Server.Core.Domain.Identity;

/// <summary>
/// One credential check of the web login, the REST API or WebDAV: who tried, from where,
/// through which transport, and the outcome (see <see cref="RepeatCount"/> for repeated lockout
/// hits). Written by the security monitor and pruned
/// after the configured retention period.
/// </summary>
public sealed class LoginAttemptRecord
{
    public long Id { get; set; }

    public DateTime AtUtc { get; set; }

    public SecurityChannel Channel { get; set; }

    /// <summary>
    /// Entered user name, trimmed and lower-cased (logins are case-insensitive); empty for
    /// <see cref="LoginOutcome.AddressLockedOut"/>, whose lock covers every user name.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Client address, when the transport knows it.</summary>
    public string? Address { get; set; }

    public LoginOutcome Outcome { get; set; }

    /// <summary>
    /// End of the lockout this attempt ran into; only set for <see cref="LoginOutcome.LockedOut"/>
    /// and <see cref="LoginOutcome.AddressLockedOut"/>.
    /// </summary>
    public DateTime? LockedUntilUtc { get; set; }

    /// <summary>
    /// Attempts this row stands for. Every attempt during a lockout returns LockedOut again, so the
    /// repeats of one lock (same channel, account and address) are folded into the row of its first
    /// LockedOut attempt; all other rows stand for exactly one attempt.
    /// </summary>
    public int RepeatCount { get; set; } = 1;
}
