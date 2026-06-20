using Kaimo_File_Server.Core.Language;

namespace Kaimo_File_Server.Core.Security;

/// <summary>
/// Configurable, globally enforced password requirements. Persisted under
/// <see cref="ConfigKey"/> via the config repository and edited from the
/// settings page ("Passwort-Anforderungen" tab). The defaults reproduce the
/// previously hard-coded behaviour (minimum length 6, no character classes),
/// so an unconfigured system keeps working unchanged.
/// </summary>
public class PasswordPolicy
{
    public const string ConfigKey = "security.password.policy";

    /// <summary>Minimum number of characters. Must be at least 1.</summary>
    public int MinLength { get; set; } = 6;

    public bool RequireUppercase { get; set; }
    public bool RequireLowercase { get; set; }
    public bool RequireDigit { get; set; }
    public bool RequireSpecial { get; set; }

    public static PasswordPolicy Default() => new();

    /// <summary>
    /// Validates a candidate password against this policy. Returns a localized
    /// error message describing the first unmet requirement, or <c>null</c> when
    /// the password satisfies the policy.
    /// </summary>
    public string? Validate(string? password)
    {
        password ??= string.Empty;

        if (password.Length < MinLength)
            return string.Format(Resources.Web_Settings_PwPolicy_TooShort, MinLength);

        if (RequireUppercase && !password.Any(char.IsUpper))
            return Resources.Web_Settings_PwPolicy_NeedUpper;

        if (RequireLowercase && !password.Any(char.IsLower))
            return Resources.Web_Settings_PwPolicy_NeedLower;

        if (RequireDigit && !password.Any(char.IsDigit))
            return Resources.Web_Settings_PwPolicy_NeedDigit;

        if (RequireSpecial && password.All(char.IsLetterOrDigit))
            return Resources.Web_Settings_PwPolicy_NeedSpecial;

        return null;
    }
}
