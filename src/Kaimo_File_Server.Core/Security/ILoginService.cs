using Kaimo_File_Server.Core.Domain.Identity;

namespace Kaimo_File_Server.Core.Security;

/// <summary>Outcome of a credential authentication attempt.</summary>
public enum LoginOutcome
{
    Success,
    InvalidCredentials,
    AccountDisabled,
    LockedOut,
}

/// <summary>
/// Result of <see cref="ILoginService.AuthenticateAsync"/>.
///
/// <see cref="LoginOutcome.InvalidCredentials"/> is returned for BOTH an unknown
/// username and a wrong password — deliberately indistinguishable, so the
/// response cannot be used to enumerate valid usernames.
/// </summary>
public sealed record LoginResult(
    LoginOutcome Outcome,
    UserContext? UserContext = null,
    TimeSpan RetryAfter = default)
{
    public static LoginResult ForSuccess(UserContext context) =>
        new(LoginOutcome.Success, context);

    public static readonly LoginResult InvalidCredentials =
        new(LoginOutcome.InvalidCredentials);

    public static readonly LoginResult AccountDisabled =
        new(LoginOutcome.AccountDisabled);

    public static LoginResult LockedOut(TimeSpan retryAfter) =>
        new(LoginOutcome.LockedOut, RetryAfter: retryAfter);
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
    Task<LoginResult> AuthenticateAsync(string username, string password);
}
