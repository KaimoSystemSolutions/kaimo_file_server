using Kaimo_File_Server.Core.Domain.Identity;

namespace Kaimo_File_Server.Core.Security.Authentication;

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

    /// <summary>
    /// Only in the security log: the password was correct, but the second factor of the web
    /// login (TOTP code or recovery code) was wrong. A strong sign that the password is known
    /// to someone else.
    /// </summary>
    SecondFactorFailed,

    /// <summary>
    /// Only in the security log: a correct password on the web login of an account that also
    /// needs a second factor. Neither a success nor a failure; the sign-in is logged as
    /// <see cref="Success"/> only once the second factor was accepted.
    /// </summary>
    PasswordAccepted,

    /// <summary>
    /// Only in the security log: the account-wide budget of second-factor codes is used up, so
    /// the web login refuses every code until the lock ends. Kept apart from
    /// <see cref="LockedOut"/> (the password budget) because an administrator's second-factor
    /// reset lifts this lock alone.
    /// </summary>
    SecondFactorLockedOut,
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
/// Lockout policy: after <see cref="MaxAttempts"/> consecutive failures a key
/// is locked for <see cref="LockoutDuration"/>.
/// </summary>
public readonly record struct LoginThrottlePolicy(int MaxAttempts, TimeSpan LockoutDuration)
{
    public static LoginThrottlePolicy Default { get; } = new(5, TimeSpan.FromMinutes(15));
}

/// <summary>Result of a throttle check: whether the key is currently locked and for how long.</summary>
public readonly record struct LockoutStatus(bool IsLockedOut, TimeSpan RetryAfter)
{
    public static readonly LockoutStatus NotLocked = new(false, TimeSpan.Zero);
}

/// <summary>Whether the web login offers or requires a second factor.</summary>
public enum SecondFactorMode
{
    /// <summary>No second factor is asked; set-up factors are kept but not used.</summary>
    Off,

    /// <summary>Users may set up a second factor; whoever has one is asked for it.</summary>
    Optional,

    /// <summary>Like <see cref="Optional"/>, and the targeted users must set one up.</summary>
    Required,
}

/// <summary>What the web login has to ask after a correct password.</summary>
public enum SecondFactorLoginStep
{
    /// <summary>Nothing more: the password is enough for this account.</summary>
    None,

    /// <summary>The account has a second factor; ask for a code or a recovery code.</summary>
    Verify,

    /// <summary>The policy requires a second factor the account does not have yet; set it up first.</summary>
    Enroll,
}
