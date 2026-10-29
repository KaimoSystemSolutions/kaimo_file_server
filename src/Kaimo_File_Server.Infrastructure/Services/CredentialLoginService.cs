using System.Net;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Configuration;

namespace Kaimo_File_Server.Infrastructure.Services;

/// <summary>
/// Default <see cref="ILoginService"/>: verifies credentials, applies
/// brute-force lockout via <see cref="ILoginThrottle"/>, and resists username
/// enumeration by always paying the password-hash cost — even for unknown
/// users — and returning an indistinguishable result for "no such user" and
/// "wrong password".
///
/// Every attempt is charged against three budgets — the client address, the
/// (user name, address) pair and the account — whose thresholds come from
/// <see cref="LoginProtectionSettings"/> so an administrator can tune them at runtime.
/// The address budget is checked first: guessing with ever-new user names from one
/// address never reaches a per-user limit, so it is refused there, before any database
/// lookup or password hash. Such a lock is reported with <see cref="LockoutScope.Address"/>,
/// because it refuses every user name without locking any account.
/// </summary>
public sealed class CredentialLoginService : ILoginService
{
    // A throwaway but valid bcrypt hash. Verifying an unknown user's password
    // against it costs the same as a real verify, so response time does not
    // leak whether the username exists. Computed once per process.
    private static readonly string DummyPasswordHash =
        BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString("N"));

    private readonly IUserRepository _users;
    private readonly IPasswordService _passwords;
    private readonly IUserContextFactory _contextFactory;
    private readonly ILoginThrottle _throttle;
    private readonly IConfigRepository _config;
    private readonly AccessRestrictionService? _access;

    public CredentialLoginService(
        IUserRepository users,
        IPasswordService passwords,
        IUserContextFactory contextFactory,
        ILoginThrottle throttle,
        IConfigRepository config,
        AccessRestrictionService? access = null)
    {
        _users = users;
        _passwords = passwords;
        _contextFactory = contextFactory;
        _throttle = throttle;
        _config = config;
        _access = access;
    }

    public async Task<LoginResult> AuthenticateAsync(
        string username, string password, string? remoteAddress = null, string? host = null)
    {
        var trimmed = (username ?? string.Empty).Trim();
        password ??= string.Empty;

        var settings = await _config.LoadLoginProtectionAsync();
        var budget = new LoginBudget(_throttle,
            (AddressThrottleKey(remoteAddress), settings.IpPolicy, LockoutScope.Address),
            (ThrottleKey(trimmed, remoteAddress), settings.UserPolicy, LockoutScope.Account),
            (AccountThrottleKey(trimmed), settings.AccountPolicy, LockoutScope.Account));

        // 1. Reject early if already locked — without a DB lookup, so a locked
        //    response is identical for existing and non-existing usernames. The
        //    attempt is reserved atomically, so parallel requests cannot all pass
        //    this gate while earlier ones are still inside the slow hash verify.
        //    The address budget, the (user, address) budget and the account-wide
        //    ceiling all apply.
        var (status, scope) = budget.Begin();
        if (status.IsLockedOut)
            return LoginResult.LockedOut(status.RetryAfter, scope);

        bool settled = false;
        try
        {
            // No login without a password. An empty password is universally invalid and reveals
            // nothing about whether the user exists, so reject it before any DB lookup but still
            // count it as a failed attempt toward lockout.
            if (password.Length == 0)
            {
                settled = true;
                return RegisterFailure(budget);
            }

            var user = await _users.GetByUsernameAsync(trimmed);

            // 2. Always verify against *some* hash so timing is independent of
            //    whether the user exists.
            var passwordOk = user is null
                ? VerifyDummy(password)
                : _passwords.VerifyPassword(password, user.PasswordHash);

            if (user is null || !passwordOk)
            {
                settled = true;
                return RegisterFailure(budget);
            }

            // 3. Valid credentials but the account is locked/disabled. Not a
            //    brute-force signal, so it does not count toward the lockout
            //    (the reservation is released in finally).
            if (!user.IsEnabled)
                return LoginResult.AccountDisabled;

            var context = await _contextFactory.CreateAsync(user);

            // 4. Valid credentials from an address an access restriction rule does not
            //    allow. Must be indistinguishable from a wrong password, or the response
            //    would confirm the password: counted as a failure (no throttle reset), and
            //    every caller shows it exactly like InvalidCredentials. The distinct outcome
            //    only reaches the admin-side security log.
            if (_access is not null && !await _access.IsAllowedAsync(context, remoteAddress, host))
            {
                settled = true;
                var failure = RegisterFailure(budget);
                return failure.Outcome == LoginOutcome.LockedOut ? failure : LoginResult.AddressNotAllowed;
            }

            // 5. Success — clear any accumulated failures for these keys.
            settled = true;
            budget.Reset();
            return LoginResult.ForSuccess(context);
        }
        finally
        {
            // Disabled accounts and infrastructure errors neither count nor leak
            // a reservation that would otherwise shrink the budget forever.
            if (!settled)
                budget.Release();
        }
    }

    private static LoginResult RegisterFailure(LoginBudget budget)
    {
        var (failure, scope) = budget.RegisterFailure();
        return failure.IsLockedOut ? LoginResult.LockedOut(failure.RetryAfter, scope) : LoginResult.InvalidCredentials;
    }

    /// <summary>
    /// Default of <see cref="LoginProtectionSettings.AccountWideFactor"/>: the account-wide
    /// budget is this many times the per-address budget. It caps guessing spread over many
    /// (possibly spoofed) client addresses, while a stranger still needs far more failures to
    /// lock the real user out than per address.
    /// </summary>
    internal const int AccountWideAttemptFactor = LoginProtectionSettings.DefaultAccountWideFactor;

    /// <summary>Key of the account-wide ceiling, independent of the client address.</summary>
    // Leading "|" cannot start a username, so this never collides with a (user, address) key.
    internal static string AccountThrottleKey(string username) => $"|account|{username.ToLowerInvariant()}";

    /// <summary>
    /// Key of the per-address budget, across all user names. The address is canonicalized
    /// (an IPv4-mapped IPv6 address counts as its IPv4 address), so one client cannot get a
    /// second budget by switching notation. Empty without a known address: the throttle skips
    /// empty keys, so no address budget applies then.
    /// </summary>
    // Leading "|" cannot start a username, so this never collides with a (user, address) key.
    internal static string AddressThrottleKey(string? remoteAddress)
    {
        var trimmed = remoteAddress?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return string.Empty;
        if (IPAddress.TryParse(trimmed, out var parsed))
            trimmed = (parsed.IsIPv4MappedToIPv6 ? parsed.MapToIPv4() : parsed).ToString();
        return $"|ip|{trimmed.ToLowerInvariant()}";
    }

    /// <summary>
    /// Lockout is tracked per (username, client address): a stranger failing
    /// logins for "admin" only locks out their own address, not the real admin.
    /// Without a known address (e.g. a caller that cannot see the client) the
    /// key falls back to the username alone.
    /// </summary>
    internal static string ThrottleKey(string username, string? remoteAddress)
    {
        var user = username.ToLowerInvariant();
        return string.IsNullOrWhiteSpace(remoteAddress) ? user : $"{user}|{remoteAddress.Trim()}";
    }

    private bool VerifyDummy(string password)
    {
        // Discard the result; only the timing cost matters.
        _ = _passwords.VerifyPassword(password, DummyPasswordHash);
        return false;
    }
}
