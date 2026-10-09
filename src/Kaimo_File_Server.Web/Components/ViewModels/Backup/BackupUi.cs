using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Web.Components.Shared;

namespace Kaimo_File_Server.Web.Components.ViewModels.Backup;

/// <summary>Localized labels and status tones shared by the backup page components.</summary>
public static class BackupUi
{
    private static string R(string key) => BackupViewModel.R(key);

    public static string BackendLabel(BackupBackend backend) => R("Web_Backup_Backend_" + backend);

    public static string RunStatusLabel(BackupRunStatus status) => R("Web_Backup_Status_" + status);

    public static string RunTypeLabel(BackupRunType type) => R("Web_Backup_RunType_" + type);

    public static string TriggerLabel(BackupRunTrigger trigger) => R("Web_Backup_Trigger_" + trigger);

    public static StatusTone RunTone(BackupRunStatus status) => status switch
    {
        BackupRunStatus.Succeeded => StatusTone.Positive,
        BackupRunStatus.SucceededWithWarnings => StatusTone.Warning,
        BackupRunStatus.Failed or BackupRunStatus.Interrupted => StatusTone.Negative,
        _ => StatusTone.Neutral,
    };

    public static StatusTone JobTone(BackupJob job)
        => !job.Enabled ? StatusTone.Neutral
            : job.LastStatus is { } status ? RunTone(status)
            : StatusTone.Neutral;

    public static string JobStatusLabel(BackupJob job)
        => !job.Enabled ? R("Web_Backup_Disabled")
            : job.LastStatus is { } status ? RunStatusLabel(status)
            : R("Web_Backup_NeverRun");

    public static StatusTone RepositoryTone(BackupRepository repo) => repo.State switch
    {
        BackupRepositoryState.Disabled => StatusTone.Neutral,
        BackupRepositoryState.PendingRecoveryKit => StatusTone.Warning,
        _ when !string.IsNullOrEmpty(repo.LastErrorCode) => StatusTone.Negative,
        _ => StatusTone.Positive,
    };

    public static string RepositoryStateLabel(BackupRepository repo) => repo.State switch
    {
        BackupRepositoryState.Disabled => R("Web_Backup_Disabled"),
        BackupRepositoryState.PendingRecoveryKit => R("Web_Backup_Repo_State_PendingKit"),
        _ when !string.IsNullOrEmpty(repo.LastErrorCode) => R("Web_Backup_Repo_State_Error"),
        _ => R("Web_Backup_Repo_State_Active"),
    };
}
