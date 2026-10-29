using Kaimo_File_Server.Core.Domain.Identity;

namespace Kaimo_File_Server.Core.Security;

/// <summary>Outcome of a credential authentication attempt.</summary>
public enum LoginOutcome
{
    Success,
    InvalidCredentials,
    AccountDisabled,
    LockedOut,

    /// <summary>
    /// Valid credentials, but an access restriction rule does not allow this user from the
    /// client address / host used (see <see cref="AccessRestrictionSettings"/>). Counted as a
    /// failed attempt and shown to the client exactly like <see cref="InvalidCredentials"/>,
    /// so it never confirms the password; only the admin-side security log tells them apart.
    /// </summary>
    AddressNotAllowed,

    /// <summary>
    /// Valid credentials, but the file service used for the login (web, client app, WebDAV)
    /// is switched off for this account or globally. Shown to the client exactly like
    /// <see cref="InvalidCredentials"/>; only the admin-side security log tells them apart.
    /// </summary>
    ServiceDisabled,

    /// <summary>
    /// Only in the security log, never returned by the login service: an attempt refused
    /// because its client address was locked (<see cref="LoginResult"/> reports
    /// <see cref="LockedOut"/> with <see cref="LockoutScope.Address"/>). Such a lock refuses
    /// every user name, so it is logged without one and never counts as a locked account.
    /// </summary>
    AddressLockedOut,
}

/// <summary>Which budget a <see cref="LoginOutcome.LockedOut"/> result comes from.</summary>
public enum LockoutScope
{
    /// <summary>The account is locked: per (user name, client address) or account-wide.</summary>
    Account,

    /// <summary>The client address is locked, across all user names; no account is locked by it.</summary>
    Address,
}

/// <summary>
/// Result of <see cref="ILoginService.AuthenticateAsync"/>.
///
/// <see cref="LoginOutcome.InvalidCredentials"/> is returned for BOTH an unknown
/// username and a wrong password — deliberately indistinguishable, so the
/// response cannot be used to enumerate valid usernames.
/// <see cref="LockScope"/> is only meaningful for <see cref="LoginOutcome.LockedOut"/>.
/// </summary>
public sealed record LoginResult(
    LoginOutcome Outcome,
    UserContext? UserContext = null,
    TimeSpan RetryAfter = default,
    LockoutScope LockScope = LockoutScope.Account)
{
    public static LoginResult ForSuccess(UserContext context) =>
        new(LoginOutcome.Success, context);

    public static readonly LoginResult InvalidCredentials =
        new(LoginOutcome.InvalidCredentials);

    public static readonly LoginResult AccountDisabled =
        new(LoginOutcome.AccountDisabled);

    public static readonly LoginResult AddressNotAllowed =
        new(LoginOutcome.AddressNotAllowed);

    public static readonly LoginResult ServiceDisabled =
        new(LoginOutcome.ServiceDisabled);

    public static LoginResult LockedOut(TimeSpan retryAfter, LockoutScope scope = LockoutScope.Account) =>
        new(LoginOutcome.LockedOut, RetryAfter: retryAfter, LockScope: scope);
}

/// <summary>
/// Authenticates a username/password pair with built-in brute-force
/// protection (see <see cref="ILoginThrottle"/>) and username-enumeration
/// resistance (constant-time verification regardless of whether the user
/// exists). Transport layers (web login, REST) call this instead of touching
/// the password hash directly.
/// </summary>
public interface ILoginService
{
    /// <param name="remoteAddress">
    /// Client address, when the transport knows it. Lockout is then tracked per
    /// (username, address), so failed attempts from one client cannot lock the
    /// account out for everyone else.
    /// </param>
    /// <param name="host">
    /// Host name the server was reached under (request <c>Host</c>), checked against the
    /// access restriction rules together with <paramref name="remoteAddress"/>.
    /// </param>
    Task<LoginResult> AuthenticateAsync(string username, string password, string? remoteAddress = null, string? host = null);
}
