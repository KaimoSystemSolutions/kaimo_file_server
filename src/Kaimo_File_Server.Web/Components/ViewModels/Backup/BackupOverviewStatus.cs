using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Infrastructure.Backup.Restic;
using Kaimo_File_Server.Web.Components.Shared;

namespace Kaimo_File_Server.Web.Components.ViewModels.Backup;

/// <summary>Why the backup overview lists an object; the overview words it.</summary>
public enum BackupAttentionReason
{
    NoRepositories,
    NoJobs,
    RepositoryKitPending,
    RepositoryError,
    RepositoryCheckFailed,
    JobFailed,
    JobOverdue,
    JobWarnings,
}

/// <summary>One status message of the backup overview; <paramref name="Id"/> is the job or repository (null for setup hints).</summary>
public sealed record BackupAttention(BackupAttentionReason Reason, StatusTone Tone, Guid? Id, string Name)
{
    public bool IsRepository => Reason is BackupAttentionReason.NoRepositories or BackupAttentionReason.RepositoryKitPending
        or BackupAttentionReason.RepositoryError or BackupAttentionReason.RepositoryCheckFailed;
}

/// <summary>
/// Reads job and repository state into the overview's status messages and health counts.
/// Uses the same rules as the job list and the start page (failed → problem; overdue or
/// warnings → warning; disabled objects are not reported).
/// </summary>
public static class BackupOverviewStatus
{
    /// <param name="repositories">Null when the actor does not manage repositories (they are not checked then).</param>
    public static IReadOnlyList<BackupAttention> Attention(
        IReadOnlyList<BackupJobView> jobs, IReadOnlyList<BackupRepositoryView>? repositories, bool canManageJobs, DateTime nowUtc)
    {
        var items = new List<BackupAttention>();
        if (repositories is { Count: 0 })
            items.Add(new(BackupAttentionReason.NoRepositories, StatusTone.Warning, null, string.Empty));
        if (jobs.Count == 0 && canManageJobs)
            items.Add(new(BackupAttentionReason.NoJobs, StatusTone.Warning, null, string.Empty));

        foreach (var repo in (repositories ?? []).Select(v => v.Repository))
        {
            BackupAttentionReason? reason = repo.State switch
            {
                BackupRepositoryState.Disabled => null,
                BackupRepositoryState.PendingRecoveryKit => BackupAttentionReason.RepositoryKitPending,
                _ when !string.IsNullOrEmpty(repo.LastErrorCode) => BackupAttentionReason.RepositoryError,
                _ when repo.LastCheckOk == false => BackupAttentionReason.RepositoryCheckFailed,
                _ => null,
            };
            if (reason is { } r)
                items.Add(new(r, r == BackupAttentionReason.RepositoryKitPending ? StatusTone.Warning : StatusTone.Negative, repo.Id, repo.Name));
        }

        foreach (var job in jobs.Select(v => v.Job).Where(j => j.Enabled))
        {
            BackupAttentionReason? reason = job.LastStatus is BackupRunStatus.Failed or BackupRunStatus.Interrupted ? BackupAttentionReason.JobFailed
                : BackupSchedulerService.IsStale(job, nowUtc) ? BackupAttentionReason.JobOverdue
                : job.LastStatus == BackupRunStatus.SucceededWithWarnings ? BackupAttentionReason.JobWarnings
                : null;
            if (reason is { } r)
                items.Add(new(r, r == BackupAttentionReason.JobFailed ? StatusTone.Negative : StatusTone.Warning, job.Id, job.Name));
        }

        // Stable sort keeps the setup hints first among the warnings.
        return items.OrderBy(i => i.Tone == StatusTone.Negative ? 0 : 1)
            .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static HealthCounts JobHealth(IReadOnlyList<BackupJobView> jobs, DateTime nowUtc)
    {
        var jobList = jobs.Select(v => v.Job).ToList();
        bool Inactive(BackupJob j) => !j.Enabled || j.LastStatus is null;
        bool Failed(BackupJob j) => !Inactive(j) && j.LastStatus is BackupRunStatus.Failed or BackupRunStatus.Interrupted;
        bool Warning(BackupJob j) => !Inactive(j) && !Failed(j)
            && (BackupSchedulerService.IsStale(j, nowUtc) || j.LastStatus == BackupRunStatus.SucceededWithWarnings);
        return new HealthCounts(
            Ok: jobList.Count(j => !Inactive(j) && !Failed(j) && !Warning(j)),
            Warning: jobList.Count(Warning),
            Failed: jobList.Count(Failed),
            Inactive: jobList.Count(Inactive));
    }

    public static HealthCounts RepositoryHealth(IReadOnlyList<BackupRepositoryView> repositories)
    {
        var tones = repositories.Select(v => BackupUi.RepositoryTone(v.Repository)).ToList();
        return new HealthCounts(
            Ok: tones.Count(t => t == StatusTone.Positive),
            Warning: tones.Count(t => t == StatusTone.Warning),
            Failed: tones.Count(t => t == StatusTone.Negative),
            Inactive: tones.Count(t => t == StatusTone.Neutral));
    }
}
