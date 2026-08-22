namespace Kaimo_File_Server.Infrastructure.Backup;

/// <summary>
/// Database-backed backup configuration (stored in <c>ConfigSettings</c> under
/// <see cref="ConfigKey"/>). Read by the scheduler (Host) and edited through the
/// Settings UI (Web). Times are interpreted in the server's local time zone.
/// </summary>
public sealed record BackupSettings
{
    public const string ConfigKey = "backup.settings";

    public const int MinRetentionCount = 1;
    public const int MaxRetentionCount = 365;
    public const int MinRetentionDays = 1;
    public const int MaxRetentionDays = 3650;

    /// <summary>Whether the daily scheduled backup runs at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Start of the daily window in which a scheduled backup may run.</summary>
    public TimeOnly WindowStart { get; set; } = new(2, 0);

    /// <summary>End of the daily window in which a scheduled backup may run.</summary>
    public TimeOnly WindowEnd { get; set; } = new(4, 0);

    /// <summary>How many scheduled backups to keep.</summary>
    public int RetentionCount { get; set; } = 14;

    /// <summary>Delete scheduled backups older than this many days.</summary>
    public int RetentionDays { get; set; } = 30;

    public void Normalize()
    {
        RetentionCount = Math.Clamp(RetentionCount, MinRetentionCount, MaxRetentionCount);
        RetentionDays = Math.Clamp(RetentionDays, MinRetentionDays, MaxRetentionDays);
    }

    public static BackupSettings Default() => new();
}
