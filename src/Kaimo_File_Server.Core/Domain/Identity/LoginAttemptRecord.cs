using Kaimo_File_Server.Core.Security;

namespace Kaimo_File_Server.Core.Domain.Identity;

/// <summary>
/// One credential check of the web login, the REST API or WebDAV: who tried, from where,
/// through which transport, and the outcome. Written by the security monitor and pruned
/// after the configured retention period.
/// </summary>
public sealed class LoginAttemptRecord
{
    public long Id { get; set; }

    public DateTime AtUtc { get; set; }

    public SecurityChannel Channel { get; set; }

    /// <summary>Entered user name, trimmed and lower-cased (logins are case-insensitive).</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Client address, when the transport knows it.</summary>
    public string? Address { get; set; }

    public LoginOutcome Outcome { get; set; }

    /// <summary>End of the lockout this attempt ran into; only set for <see cref="LoginOutcome.LockedOut"/>.</summary>
    public DateTime? LockedUntilUtc { get; set; }
}
