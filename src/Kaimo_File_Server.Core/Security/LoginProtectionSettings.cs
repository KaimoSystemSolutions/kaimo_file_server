namespace Kaimo_File_Server.Core.Security;

/// <summary>
/// Runtime-configurable brute-force protection of the credential login (web UI, client API and
/// WebDAV Basic all go through the same login service). Bundles every threshold with its config
/// key, default and bounds, so the login service and the settings page cannot drift apart.
///
/// Three budgets apply to each attempt:
/// <list type="bullet">
///   <item>per (user name, client address): <see cref="MaxAttempts"/> failures lock that pair for <see cref="LockoutMinutes"/>;</item>
///   <item>per account, across all addresses: <see cref="MaxAttempts"/> × <see cref="AccountWideFactor"/> failures;</item>
///   <item>per client address, across all user names: <see cref="IpMaxAttempts"/> failures lock the address for <see cref="IpLockoutMinutes"/>.</item>
/// </list>
/// </summary>
public sealed record LoginProtectionSettings
{
    // The two per-user keys predate this type; keeping them preserves tuned values of existing installations.
    public const string MaxAttemptsKey = "app.user.maxLoginAttempts";
    public const string LockoutMinutesKey = "app.user.lockoutMinutes";
    public const string AccountWideFactorKey = "security.login.accountWideFactor";
    public const string IpMaxAttemptsKey = "security.login.ipMaxAttempts";
    public const string IpLockoutMinutesKey = "security.login.ipLockoutMinutes";

    public const int DefaultMaxAttempts = 5;
    public const int DefaultLockoutMinutes = 15;
    public const int DefaultAccountWideFactor = 10;
    public const int DefaultIpMaxAttempts = 15;
    public const int DefaultIpLockoutMinutes = 30;

    /// <summary>Upper bound of every attempt budget; beyond it the protection is effectively off.</summary>
    public const int MaxAttemptsLimit = 1000;

    /// <summary>Upper bound of every lockout duration (seven days).</summary>
    public const int MaxLockoutMinutes = 7 * 24 * 60;

    public const int MaxAccountWideFactor = 100;

    public static LoginProtectionSettings Default { get; } = new();

    /// <summary>Failed attempts per (user name, client address) before that pair is locked.</summary>
    public int MaxAttempts { get; init; } = DefaultMaxAttempts;

    /// <summary>Lockout of a (user name, client address) pair and of the account-wide ceiling, in minutes.</summary>
    public int LockoutMinutes { get; init; } = DefaultLockoutMinutes;

    /// <summary>The account-wide ceiling is this many times <see cref="MaxAttempts"/>.</summary>
    public int AccountWideFactor { get; init; } = DefaultAccountWideFactor;

    /// <summary>Failed attempts per client address, across all user names, before the address is locked.</summary>
    public int IpMaxAttempts { get; init; } = DefaultIpMaxAttempts;

    /// <summary>Lockout of a client address, in minutes.</summary>
    public int IpLockoutMinutes { get; init; } = DefaultIpLockoutMinutes;

    public LoginThrottlePolicy UserPolicy => new(MaxAttempts, TimeSpan.FromMinutes(LockoutMinutes));

    public LoginThrottlePolicy AccountPolicy => new(MaxAttempts * AccountWideFactor, TimeSpan.FromMinutes(LockoutMinutes));

    public LoginThrottlePolicy IpPolicy => new(IpMaxAttempts, TimeSpan.FromMinutes(IpLockoutMinutes));

    /// <summary>
    /// Replaces unusable values (zero or negative) with the defaults and clamps values above the
    /// upper bounds, so a mistyped setting can neither disable nor overflow the protection.
    /// </summary>
    public LoginProtectionSettings Normalize() => new()
    {
        MaxAttempts = Bound(MaxAttempts, DefaultMaxAttempts, MaxAttemptsLimit),
        LockoutMinutes = Bound(LockoutMinutes, DefaultLockoutMinutes, MaxLockoutMinutes),
        AccountWideFactor = Bound(AccountWideFactor, DefaultAccountWideFactor, MaxAccountWideFactor),
        IpMaxAttempts = Bound(IpMaxAttempts, DefaultIpMaxAttempts, MaxAttemptsLimit),
        IpLockoutMinutes = Bound(IpLockoutMinutes, DefaultIpLockoutMinutes, MaxLockoutMinutes),
    };

    private static int Bound(int value, int fallback, int max) => value <= 0 ? fallback : Math.Min(value, max);
}
