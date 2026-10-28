using System.Globalization;
using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Web.Components.Shared;

namespace Kaimo_File_Server.Web.Components.ViewModels.Backup;

/// <summary>Localized labels and status tones shared by the backup page components.</summary>
public static class BackupUi
{
    private static string R(string key) => BackupViewModel.R(key);

    public static string BackendLabel(BackupBackend backend) => R("Web_Backup_Backend_" + backend);

    // B2, Azure, GCS and Swift are hidden in the UI only: they are implemented and still work
    // for existing repositories, but have had too little testing to be offered for new ones.
    private static readonly BackupBackend[] OfferedBackends =
        [BackupBackend.Local, BackupBackend.Sftp, BackupBackend.Smb, BackupBackend.Rest, BackupBackend.Rclone, BackupBackend.S3];

    /// <summary>Storage types of the editor's dropdown; a hidden type stays listed while its repository is edited.</summary>
    public static IReadOnlyList<BackupBackend> SelectableBackends(BackupBackend current)
        => OfferedBackends.Contains(current) ? OfferedBackends : [.. OfferedBackends, current];

    /// <summary>The part of a local repository path below <paramref name="prefix"/> (ends with '/'); null when outside.</summary>
    public static string? LocalSubPath(string path, string prefix)
        => string.IsNullOrEmpty(path) ? string.Empty
            : path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..]
            : null;

    // ── icon colours: jobs as on the start page's backup tile, repositories like other storage
    //    targets (connections), shares and pools in their entity colours ──

    public static readonly string JobIconClass = TileColor.Group.IconClass();
    public static readonly string RepositoryIconClass = TileColor.Info.IconClass();
    public static readonly string ShareIconClass = TileColor.Folder.IconClass();
    public static readonly string PoolIconClass = TileColor.Pool.IconClass();

    public static string RunIcon(BackupRunType type) => type switch
    {
        BackupRunType.Backup => "upload.svg",
        BackupRunType.Restore => "cloud-download.svg",
        BackupRunType.Check or BackupRunType.TestRestore => "shield.svg",
        BackupRunType.Copy => "copy.svg",
        // Retention keeps a selection of backup points; only pruning really frees data.
        BackupRunType.Forget => "filter.svg",
        BackupRunType.Prune => "trash.svg",
        _ => "archive.svg",
    };

    /// <summary>Backups belong to a job, restores to a share, everything else is repository maintenance.</summary>
    public static string RunIconClass(BackupRunType type) => type switch
    {
        BackupRunType.Backup => JobIconClass,
        BackupRunType.Restore => ShareIconClass,
        _ => RepositoryIconClass,
    };

    public static string RunStatusLabel(BackupRunStatus status) => R("Web_Backup_Status_" + status);

    public static string RunTypeLabel(BackupRunType type) => R("Web_Backup_RunType_" + type);

    public static string TriggerLabel(BackupRunTrigger trigger) => R("Web_Backup_Trigger_" + trigger);

    public static string LevelLabel(BackupContentLevel level) => R("Web_Backup_Level_" + level);

    public static string PoolName(string poolPath) => Path.GetFileName(Path.TrimEndingDirectorySeparator(poolPath));
    /// <summary>Name of a source in a run outcome; the system source is stored as a key and shown in the UI language.</summary>
    public static string SourceName(string name)
        => name == Infrastructure.Backup.Restic.BackupJobExecutor.SystemSourceKey ? R("Web_Backup_Source_System") : name;

    /// <summary>"Projects, Pool pool01, Database &amp; system": every source of a job.</summary>
    public static string SourcesText(Infrastructure.Backup.Restic.BackupJobView view)
    {
        var parts = view.Shares.Select(s => s.Name)
            .Concat(view.Job.Sources.Where(s => s.Kind == BackupSourceKind.Pool && s.PoolPath is not null)
                .Select(s => string.Format(CultureInfo.CurrentCulture, R("Web_Backup_Source_Pool"), PoolName(s.PoolPath!))))
            .ToList();
        if (view.Job.ContentLevel != BackupContentLevel.Files)
            parts.Add(R("Web_Backup_Source_System"));
        return string.Join(", ", parts);
    }

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

    /// <param name="formatTime">Formats a slot in the configured time format (DateFormatService.FormatTime).</param>
    public static string ScheduleText(BackupSchedule schedule, Func<TimeOnly, string> formatTime)
    {
        if (schedule.IsEmpty)
            return R("Web_Backup_Schedule_ManualOnly");
        var days = schedule.Days.Count == 7
            ? R("Web_Backup_Schedule_Daily")
            : string.Join(", ", WeekDays.Where(schedule.Days.Contains).Select(DayName));
        return days + " · " + string.Join(", ", schedule.Times.Select(formatTime));
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
