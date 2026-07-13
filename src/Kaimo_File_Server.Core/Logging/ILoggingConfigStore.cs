namespace Kaimo_File_Server.Core.Logging;

/// <summary>
/// Cross-process store for the single global application log level. Written by the
/// Web settings UI, read by every process (Web + SMB host) through a periodic reload
/// so a level change takes effect within seconds without a restart. Mirrors the
/// <c>ISmbConfigStore</c> / <c>ISearchConfigStore</c> pattern.
/// </summary>
public interface ILoggingConfigStore
{
    /// <summary>Returns the configured level name, or <see cref="LoggingConfigKeys.DefaultLevel"/>.</summary>
    Task<string> GetLevelAsync();

    /// <summary>Persists the global level. Ignored if not one of <see cref="LoggingConfigKeys.AllowedLevels"/>.</summary>
    Task SetLevelAsync(string level);
}

/// <summary>Config keys and allowed values for the global log level.</summary>
public static class LoggingConfigKeys
{
    /// <summary>Config-store key holding the level name.</summary>
    public const string LevelKey = "logging.level";

    /// <summary>Default when nothing is stored: only warnings and errors.</summary>
    public const string DefaultLevel = "Warning";

    /// <summary>
    /// The four levels exposed in the UI, most→least verbose. "Error" is the highest
    /// threshold and still lets Critical/Fatal through (they sit above Error).
    /// </summary>
    public static readonly string[] AllowedLevels = ["Debug", "Information", "Warning", "Error"];

    /// <summary>True if <paramref name="level"/> is one of the allowed level names (case-insensitive).</summary>
    public static bool IsAllowed(string? level)
        => level is not null && Array.Exists(AllowedLevels,
            l => string.Equals(l, level, StringComparison.OrdinalIgnoreCase));
}
