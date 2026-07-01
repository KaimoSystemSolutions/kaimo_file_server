namespace Kaimo_File_Server.Core.Security;

/// <summary>
/// Runtime-configurable session-security settings (stored via IConfigRepository, editable on the
/// settings page). Shared between the settings UI and the authentication-state provider so the key
/// and bounds cannot drift apart.
/// </summary>
public static class SessionSecuritySettings
{
    /// <summary>
    /// How often (seconds) an active web session's account status is re-checked against the
    /// database, so a disabled/removed account loses its session within this window.
    /// </summary>
    public const string RevalidationSecondsKey = "app.session.revalidationSeconds";

    public const int DefaultRevalidationSeconds = 30;

    /// <summary>Lower bound — avoids hammering the DB with per-circuit checks.</summary>
    public const int MinRevalidationSeconds = 5;

    /// <summary>Upper bound — one hour; beyond this a "session" check is meaningless.</summary>
    public const int MaxRevalidationSeconds = 3600;

    /// <summary>Clamps a requested interval into the allowed range.</summary>
    public static int ClampRevalidationSeconds(int seconds)
        => Math.Clamp(seconds, MinRevalidationSeconds, MaxRevalidationSeconds);
}
