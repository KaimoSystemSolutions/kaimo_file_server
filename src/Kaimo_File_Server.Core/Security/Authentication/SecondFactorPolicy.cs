namespace Kaimo_File_Server.Core.Security.Authentication;

/// <summary>
/// Administrator policy for the TOTP second factor of the web login (config key
/// <see cref="ConfigKey"/>). Applies to the web login only; the client app, WebDAV and SMB
/// keep authenticating with the password alone.
/// </summary>
public sealed class SecondFactorPolicy
{
    public const string ConfigKey = "security.mfa.policy";

    /// <summary>
    /// Break-glass switch: when set to <c>true</c> on the web container, no second factor is
    /// asked at all (e.g. the last administrator lost the authenticator and the recovery codes).
    /// </summary>
    public const string DisableEnvironmentVariable = "KAIMO_MFA_DISABLED";

    public const string DefaultIssuer = "Kaimo File Server";
    public const int MaxIssuerLength = 64;

    public SecondFactorMode Mode { get; set; } = SecondFactorMode.Optional;

    /// <summary>With <see cref="SecondFactorMode.Required"/>: every user, regardless of the lists.</summary>
    public bool AppliesToAll { get; set; }

    public List<Guid> UserIds { get; set; } = new();
    public List<Guid> GroupIds { get; set; } = new();

    /// <summary>Roles held in any scope (system wide, department or share).</summary>
    public List<Guid> RoleIds { get; set; } = new();

    /// <summary>Departments including their sub-departments.</summary>
    public List<Guid> DepartmentIds { get; set; } = new();

    /// <summary>With <see cref="SecondFactorMode.Required"/>: also everyone holding any management permission.</summary>
    public bool RequireForManagementRoles { get; set; } = true;

    /// <summary>Issuer name the authenticator app shows next to the account.</summary>
    public string Issuer { get; set; } = DefaultIssuer;

    public static SecondFactorPolicy Default() => new();

    public static bool DisabledByEnvironment
        => string.Equals(Environment.GetEnvironmentVariable(DisableEnvironmentVariable), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>The issuer to use: trimmed, capped, never empty.</summary>
    public string EffectiveIssuer
    {
        get
        {
            var trimmed = (Issuer ?? "").Trim();
            if (trimmed.Length == 0) return DefaultIssuer;
            return trimmed.Length > MaxIssuerLength ? trimmed[..MaxIssuerLength] : trimmed;
        }
    }

    /// <summary>
    /// Whether the policy requires a second factor for a user with these principals.
    /// <paramref name="departmentIds"/> must already include department ancestors and
    /// <paramref name="roleIds"/> the roles of every scope.
    /// </summary>
    public bool Requires(Guid userId, IReadOnlyCollection<Guid> groupIds, IReadOnlyCollection<Guid> roleIds,
        IReadOnlyCollection<Guid> departmentIds, bool hasManagementPermission)
    {
        if (Mode != SecondFactorMode.Required)
            return false;
        return AppliesToAll
               || (RequireForManagementRoles && hasManagementPermission)
               || UserIds.Contains(userId)
               || GroupIds.Any(groupIds.Contains)
               || RoleIds.Any(roleIds.Contains)
               || DepartmentIds.Any(departmentIds.Contains);
    }

    public SecondFactorPolicy Clone() => new()
    {
        Mode = Mode,
        AppliesToAll = AppliesToAll,
        UserIds = UserIds.ToList(),
        GroupIds = GroupIds.ToList(),
        RoleIds = RoleIds.ToList(),
        DepartmentIds = DepartmentIds.ToList(),
        RequireForManagementRoles = RequireForManagementRoles,
        Issuer = Issuer,
    };
}
