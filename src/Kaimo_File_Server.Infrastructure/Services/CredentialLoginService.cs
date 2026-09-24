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
/// Lockout thresholds come from config (<c>app.user.maxLoginAttempts</c>,
/// <c>app.user.lockoutMinutes</c>) so an administrator can tune them at runtime.
/// </summary>
public sealed class CredentialLoginService : ILoginService
{
    // A throwaway but valid bcrypt hash. Verifying an unknown user's password
    // against it costs the same as a real verify, so response time does not
    // leak whether the username exists. Computed once per process.
    private static readonly string DummyPasswordHash =
        BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString("N"));

    private const string MaxAttemptsKey = "app.user.maxLoginAttempts";
    private const string LockoutMinutesKey = "app.user.lockoutMinutes";

    private readonly IUserRepository _users;
    private readonly IPasswordService _passwords;
    private readonly IUserContextFactory _contextFactory;
    private readonly ILoginThrottle _throttle;
    private readonly IConfigRepository _config;

    public CredentialLoginService(
        IUserRepository users,
        IPasswordService passwords,
        IUserContextFactory contextFactory,
        ILoginThrottle throttle,
        IConfigRepository config)
    {
        _users = users;
        _passwords = passwords;
        _contextFactory = contextFactory;
        _throttle = throttle;
        _config = config;
    }

    public async Task<LoginResult> AuthenticateAsync(
        string username, string password, string? remoteAddress = null)
    {
        var trimmed = (username ?? string.Empty).Trim();
        var key = ThrottleKey(trimmed, remoteAddress);
        password ??= string.Empty;

        var policy = await ResolvePolicyAsync();

        // 1. Reject early if already locked — without a DB lookup, so a locked
        //    response is identical for existing and non-existing usernames. The
        //    attempt is reserved atomically, so parallel requests cannot all pass
        //    this gate while earlier ones are still inside the slow hash verify.
        var status = _throttle.BeginAttempt(key, policy);
        if (status.IsLockedOut)
            return LoginResult.LockedOut(status.RetryAfter);

        bool settled = false;
        try
        {
            // No login without a password. An empty password is universally invalid and reveals
            // nothing about whether the user exists, so reject it before any DB lookup but still
            // count it as a failed attempt toward lockout.
            if (password.Length == 0)
            {
                settled = true;
                var emptyPwFailure = _throttle.RegisterFailure(key, policy);
                return emptyPwFailure.IsLockedOut
                    ? LoginResult.LockedOut(emptyPwFailure.RetryAfter)
                    : LoginResult.InvalidCredentials;
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
                var failure = _throttle.RegisterFailure(key, policy);
                return failure.IsLockedOut
                    ? LoginResult.LockedOut(failure.RetryAfter)
                    : LoginResult.InvalidCredentials;
            }

            // 3. Valid credentials but the account is locked/disabled. Not a
            //    brute-force signal, so it does not count toward the lockout
            //    (the reservation is released in finally).
            if (!user.IsEnabled)
                return LoginResult.AccountDisabled;

            // 4. Success — clear any accumulated failures for this key.
            settled = true;
            _throttle.Reset(key);
            var context = await _contextFactory.CreateAsync(user);
            return LoginResult.ForSuccess(context);
        }
        finally
        {
            // Disabled accounts and infrastructure errors neither count nor leak
            // a reservation that would otherwise shrink the budget forever.
            if (!settled)
                _throttle.EndAttempt(key);
        }
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

    private async Task<LoginThrottlePolicy> ResolvePolicyAsync()
    {
        var maxAttempts = await _config.GetIntAsync(MaxAttemptsKey, 5);
        var lockoutMinutes = await _config.GetIntAsync(LockoutMinutesKey, 15);

        if (maxAttempts <= 0) maxAttempts = 5;
        if (lockoutMinutes <= 0) lockoutMinutes = 15;

        return new LoginThrottlePolicy(maxAttempts, TimeSpan.FromMinutes(lockoutMinutes));
    }
}
