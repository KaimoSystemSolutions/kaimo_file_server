namespace Kaimo_File_Server.Infrastructure.Backup;

/// <summary>
/// Why a database backup was created. Encoded into the backup file name so the
/// retention policy and the UI can distinguish the sources.
/// </summary>
public enum BackupTrigger
{
    /// <summary>Automatic backup created by the daily scheduler.</summary>
    Scheduled,

    /// <summary>Safety backup taken automatically before pending migrations are applied.</summary>
    PreMigration,

    /// <summary>Backup created on demand from the Web UI.</summary>
    Manual,
}

public static class BackupTriggerExtensions
{
    /// <summary>Lower-case token used inside backup file names.</summary>
    public static string ToFileToken(this BackupTrigger trigger) => trigger switch
    {
        BackupTrigger.Scheduled => "scheduled",
        BackupTrigger.PreMigration => "premigration",
        BackupTrigger.Manual => "manual",
        _ => "unknown",
    };

    public static bool TryParseFileToken(string? token, out BackupTrigger trigger)
    {
        switch (token)
        {
            case "scheduled": trigger = BackupTrigger.Scheduled; return true;
            case "premigration": trigger = BackupTrigger.PreMigration; return true;
            case "manual": trigger = BackupTrigger.Manual; return true;
            default: trigger = BackupTrigger.Manual; return false;
        }
    }
}
