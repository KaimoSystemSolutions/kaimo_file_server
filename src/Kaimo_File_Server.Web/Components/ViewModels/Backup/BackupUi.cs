using System.Globalization;
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
        _ when !string.IsNullOrEmpty(repo.LastErrorCode) || repo.LastCheckOk == false => StatusTone.Negative,
        _ => StatusTone.Positive,
    };

    public static string RepositoryStateLabel(BackupRepository repo) => repo.State switch
    {
        BackupRepositoryState.Disabled => R("Web_Backup_Disabled"),
        BackupRepositoryState.PendingRecoveryKit => R("Web_Backup_Repo_State_PendingKit"),
        _ when !string.IsNullOrEmpty(repo.LastErrorCode) => R("Web_Backup_Repo_State_Error"),
        _ when repo.LastCheckOk == false => R("Web_Backup_Repo_State_CheckFailed"),
        _ => R("Web_Backup_Repo_State_Active"),
    };

    // ── schedules ──

    /// <summary>Monday first, as on the server side weekdays are plain DayOfWeek values.</summary>
    public static readonly DayOfWeek[] WeekDays =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    public static string DayName(DayOfWeek day)
        => CultureInfo.CurrentUICulture.DateTimeFormat.GetAbbreviatedDayName(day);

    public static string ScheduleText(BackupSchedule schedule)
    {
        if (schedule.IsEmpty)
            return R("Web_Backup_Schedule_ManualOnly");
        var days = schedule.Days.Count == 7
            ? R("Web_Backup_Schedule_Daily")
            : string.Join(", ", WeekDays.Where(schedule.Days.Contains).Select(DayName));
        return days + " · " + string.Join(", ", schedule.Times.Select(t => t.ToString("HH:mm", CultureInfo.InvariantCulture)));
    }

    /// <summary>"7 daily · 4 weekly · 12 monthly", or "keep all".</summary>
    public static string RetentionText(BackupRetention retention)
    {
        if (retention.IsEmpty)
            return R("Web_Backup_Retention_KeepAll");
        var parts = new List<string>();
        void Add(int value, string key)
        {
            if (value > 0)
                parts.Add(string.Format(CultureInfo.CurrentCulture, R(key), value));
        }
        Add(retention.KeepLast, "Web_Backup_Retention_LastValue");
        Add(retention.KeepDaily, "Web_Backup_Retention_DailyValue");
        Add(retention.KeepWeekly, "Web_Backup_Retention_WeeklyValue");
        Add(retention.KeepMonthly, "Web_Backup_Retention_MonthlyValue");
        Add(retention.KeepYearly, "Web_Backup_Retention_YearlyValue");
        return string.Join(" · ", parts);
    }
}
