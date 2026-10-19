using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Backup.Restic;

public partial class BackupCatalogService
{
    // ══════════════════════════════════════════
    //  Jobs (scoped)
    // ══════════════════════════════════════════

    public async Task<IReadOnlyList<BackupJobView>> ListJobsAsync(UserContext actor)
    {
        var access = await GetAccessAsync(actor);
        if (!access.CanManageJobs)
            return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var jobs = await db.BackupJobs.AsNoTracking().Include(j => j.Sources).OrderBy(j => j.Name).ToListAsync();
        var repoNames = await db.BackupRepositories.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Name);
        var shareNames = await db.ShareDefinitions.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name);
        return jobs
            .Where(j => IsJobVisible(access, j))
            .Select(j => new BackupJobView(j, repoNames.GetValueOrDefault(j.RepositoryId) ?? "?",
                j.Sources.Where(s => s.ShareId is not null)
                    .Select(s => (s.ShareId!.Value, shareNames.GetValueOrDefault(s.ShareId!.Value) ?? "?")).ToList()))
            .ToList();
    }

    /// <summary>
    /// A scoped admin sees a job only if every source is a share within their scope and the job
    /// backs up files only (database dumps hold every user's data).
    /// </summary>
    public static bool IsJobVisible(BackupAccess access, BackupJob job)
        => access.CanManageJobs
           && (access.JobsUnrestricted
               || (job.ContentLevel == BackupContentLevel.Files
                   && job.Sources.Count > 0 && job.Sources.All(s => s.Kind == BackupSourceKind.Share
                                                                    && s.ShareId is { } id && access.JobShareIds.Contains(id))));

    /// <summary>Pools a Global job admin may add as job sources (empty for everyone else).</summary>
    public async Task<IReadOnlyList<string>> ListSelectablePoolsAsync(UserContext actor)
    {
        var access = await GetAccessAsync(actor);
        return access.JobsUnrestricted ? resolver.Pools : [];
    }

    /// <summary>Shares the actor may add as job sources. The homes share is Global-only.</summary>
    public async Task<IReadOnlyList<ShareDefinition>> ListSelectableSharesAsync(UserContext actor)
    {
        var access = await GetAccessAsync(actor);
        if (!access.CanManageJobs)
            return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var shares = await db.ShareDefinitions.AsNoTracking().OrderBy(s => s.Name).ToListAsync();
        return shares.Where(s => access.JobsUnrestricted || (!s.IsUserHomes && access.JobShareIds.Contains(s.Id))).ToList();
    }

    /// <summary>
    /// Active repositories a job with these sources may use: everything for a Global job
    /// admin, otherwise repositories released to (an ancestor of) every source share's department.
    /// </summary>
    public async Task<IReadOnlyList<BackupRepository>> ListUsableRepositoriesAsync(UserContext actor, IReadOnlyCollection<Guid> shareIds)
    {
        var access = await GetAccessAsync(actor);
        if (!access.CanManageJobs)
            return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var repos = await db.BackupRepositories.AsNoTracking()
            .Where(r => r.State == BackupRepositoryState.Active).OrderBy(r => r.Name).ToListAsync();
        if (access.JobsUnrestricted)
            return repos;

        var releases = await db.BackupRepositoryDepartments.AsNoTracking().ToListAsync();
        var parents = await db.Departments.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.ParentDepartmentId);
        var shareDepartments = await db.ShareDefinitions.AsNoTracking()
            .Where(s => shareIds.Contains(s.Id)).Select(s => s.DepartmentId).Distinct().ToListAsync();
        return repos.Where(r =>
        {
            var released = releases.Where(x => x.RepositoryId == r.Id).Select(x => x.DepartmentId).ToHashSet();
            return shareDepartments.All(d => IsReleasedTo(released, d, parents));
        }).ToList();
    }

    /// <summary>All choices for the job form at once, so the form can filter without round trips.</summary>
    public async Task<BackupRepositoryChoices> GetRepositoryChoicesAsync(UserContext actor)
    {
        var shares = await ListSelectableSharesAsync(actor);
        var access = await GetAccessAsync(actor);
        await using var db = await dbFactory.CreateDbContextAsync();
        var repos = await db.BackupRepositories.AsNoTracking()
            .Where(r => r.State == BackupRepositoryState.Active).OrderBy(r => r.Name)
            .Select(r => new BackupRepositoryChoice(r.Id, r.Name, r.Backend)).ToListAsync();
        var all = repos.Select(r => r.Id).ToHashSet();
        var map = new Dictionary<Guid, IReadOnlySet<Guid>>();
        if (access.JobsUnrestricted)
        {
            foreach (var share in shares)
                map[share.Id] = all;
            return new BackupRepositoryChoices(repos, map);
        }

        var releases = await db.BackupRepositoryDepartments.AsNoTracking().ToListAsync();
        var parents = await db.Departments.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.ParentDepartmentId);
        foreach (var share in shares)
            map[share.Id] = repos.Where(r => IsReleasedTo(
                releases.Where(x => x.RepositoryId == r.Id).Select(x => x.DepartmentId).ToHashSet(),
                share.DepartmentId, parents)).Select(r => r.Id).ToHashSet();
        return new BackupRepositoryChoices(repos, map);
    }

    /// <summary>SSH connections (sftp, rsync-ssh) an SFTP repository can reuse (Global).</summary>
    public async Task<IReadOnlyList<(Guid Id, string Name)>> ListSshConnectionsAsync(UserContext actor)
    {
        await RequireRepositoriesAsync(actor);
        await using var db = await dbFactory.CreateDbContextAsync();
        var ids = ResticBackend.SshProviderIds.ToList();
        return (await db.StorageConnections.AsNoTracking()
                .Where(c => ids.Contains(c.ProviderId))
                .OrderBy(c => c.Name)
                .Select(c => new { c.Id, c.Name }).ToListAsync())
            .Select(c => (c.Id, c.Name)).ToList();
    }

    /// <summary>Released to the department itself, one of its ancestors, or the Global department.</summary>
    public static bool IsReleasedTo(IReadOnlySet<Guid> released, Guid departmentId, IReadOnlyDictionary<Guid, Guid?> parents)
    {
        if (released.Contains(WellKnownGUIDs.DEPARTMENT_GLOBAL))
            return true;
        var seen = new HashSet<Guid>();
        Guid? current = departmentId;
        while (current is { } id && seen.Add(id))
        {
            if (released.Contains(id))
                return true;
            current = parents.GetValueOrDefault(id);
        }
        return false;
    }

    public async Task<BackupJob> SaveJobAsync(UserContext actor, BackupJobDraft draft)
    {
        var access = await RequireJobsAsync(actor);
        RequireWritable();
        ValidateName(draft.Name);

        var shareIds = draft.ShareIds.Distinct().ToList();
        var poolPaths = (draft.PoolPaths ?? []).Select(Path.TrimEndingDirectorySeparator).Distinct(StringComparer.Ordinal).ToList();
        var level = draft.ContentLevel;
        if (!Enum.IsDefined(level))
            throw new ResticException("request_invalid", "Unknown content level.");
        // A database / Full job may consist of its system backup point alone.
        if (shareIds.Count == 0 && poolPaths.Count == 0 && level == BackupContentLevel.Files)
            throw new ResticException("sources_required", "Select at least one share.");
        if (!access.JobsUnrestricted && (poolPaths.Count > 0 || level != BackupContentLevel.Files))
            throw new UnauthorizedAccessException("Pools and database backups need a global administrator.");
        // A pool already contains its shares: one job backs up either shares or whole pools.
        if (shareIds.Count > 0 && poolPaths.Count > 0)
            throw new ResticException("sources_mixed", "Select either shares or pools, not both.");
        if (draft.Times.Count > MaximumScheduleTimes)
            throw new ResticException("schedule_invalid", "Too many start times.");
        var retention = draft.Retention ?? new BackupRetention();
        if (new[] { retention.KeepLast, retention.KeepDaily, retention.KeepWeekly, retention.KeepMonthly, retention.KeepYearly }
                .Any(v => v is < 0 or > BackupRetention.MaximumValue)
            || draft.AlertAfterDays is < 0 or > MaximumAlertAfterDays)
            throw new ResticException("retention_invalid", "The retention or alert values are out of range.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var shares = await db.ShareDefinitions.AsNoTracking().Where(s => shareIds.Contains(s.Id)).ToListAsync();
        if (shares.Count != shareIds.Count)
            throw new ResticException("share_missing", "A selected share no longer exists.");
        if (!access.JobsUnrestricted)
        {
            if (shares.Any(s => s.IsUserHomes || !access.JobShareIds.Contains(s.Id)))
                throw new UnauthorizedAccessException("A selected share is outside the actor's backup scope.");
            if (shares.Select(s => s.DepartmentId).Distinct().Count() > 1)
                throw new ResticException("sources_multi_department", "Jobs spanning several departments need a global administrator.");
        }

        var usable = await ListUsableRepositoriesAsync(actor, shareIds);

        BackupJob job;
        var now = time.GetUtcNow().UtcDateTime;
        if (draft.Id is { } id)
        {
            job = await db.BackupJobs.Include(j => j.Sources).FirstOrDefaultAsync(j => j.Id == id)
                  ?? throw new ResticException("job_missing", "The backup job no longer exists.");
            if (!IsJobVisible(access, job))
                throw new UnauthorizedAccessException("The job is outside the actor's backup scope.");
            // Keeping the current repository is allowed even if it is no longer active.
            if (job.RepositoryId != draft.RepositoryId && usable.All(r => r.Id != draft.RepositoryId))
                throw new ResticException("repository_not_usable", "The repository is not available for these shares.");
        }
        else
        {
            if (usable.All(r => r.Id != draft.RepositoryId))
                throw new ResticException("repository_not_usable", "The repository is not available for these shares.");
            job = new BackupJob { CreatedByUserId = actor.User.Id, CreatedAtUtc = now, LastScheduledSlotUtc = now };
            db.BackupJobs.Add(job);
        }

        if (await db.BackupJobs.AnyAsync(j => j.Id != job.Id && j.Name == draft.Name.Trim()))
            throw new ResticException("name_taken", "A backup job with this name already exists.");
        // Kept pools may be unmounted right now; only newly added ones must exist.
        var pools = resolver.Pools;
        if (poolPaths.Any(p => !pools.Contains(p, StringComparer.Ordinal) && job.Sources.All(s => s.PoolPath != p)))
            throw new ResticException("pool_missing", "A selected pool does not exist.");

        var schedule = NormalizeSchedule(draft.Times, draft.Days);
        // A changed schedule starts counting from now: a newly added time that already
        // passed today must not trigger an immediate "catch-up" run.
        if (BackupSchedule.Serialize(schedule) != BackupSchedule.Serialize(job.Schedule) || !job.Enabled && draft.Enabled)
            job.LastScheduledSlotUtc = now;

        job.Name = draft.Name.Trim();
        job.RepositoryId = draft.RepositoryId;
        job.Enabled = draft.Enabled;
        job.IncludeRecycleBin = draft.IncludeRecycleBin;
        job.ContentLevel = level;
        job.IncludeVersions = draft.IncludeVersions && poolPaths.Count > 0 && level != BackupContentLevel.Files;
        job.Schedule = schedule;
        job.Retention = new BackupRetention
        {
            KeepLast = retention.KeepLast,
            KeepDaily = retention.KeepDaily,
            KeepWeekly = retention.KeepWeekly,
            KeepMonthly = retention.KeepMonthly,
            KeepYearly = retention.KeepYearly,
        };
        job.AlertAfterDays = draft.AlertAfterDays;
        job.UpdatedAtUtc = now;

        var removed = job.Sources.Where(s => s.Kind == BackupSourceKind.Pool
            ? !poolPaths.Contains(s.PoolPath ?? string.Empty, StringComparer.Ordinal)
            : s.ShareId is null || !shareIds.Contains(s.ShareId.Value)).ToList();
        foreach (var source in removed)
            job.Sources.Remove(source);
        db.BackupJobSources.RemoveRange(removed);
        foreach (var shareId in shareIds.Where(sid => job.Sources.All(s => s.ShareId != sid)))
            job.Sources.Add(new BackupJobSource { JobId = job.Id, Kind = BackupSourceKind.Share, ShareId = shareId });
        foreach (var pool in poolPaths.Where(p => job.Sources.All(s => s.PoolPath != p)))
            job.Sources.Add(new BackupJobSource { JobId = job.Id, Kind = BackupSourceKind.Pool, PoolPath = pool });

        await db.SaveChangesAsync();
        return job;
    }

    /// <summary>Without start times or without weekdays a schedule means "manual only"; it is stored fully empty.</summary>
    private static BackupSchedule NormalizeSchedule(IReadOnlyCollection<TimeOnly> times, IReadOnlyCollection<DayOfWeek> days)
    {
        if (times.Count > MaximumScheduleTimes)
            throw new ResticException("schedule_invalid", "Too many start times.");
        var manualOnly = times.Count == 0 || days.Count == 0;
        return new BackupSchedule
        {
            Times = manualOnly ? [] : times.Distinct().Order().ToList(),
            Days = manualOnly ? [] : days.Distinct().Order().ToList(),
        };
    }

    public async Task DeleteJobAsync(UserContext actor, Guid jobId)
    {
        var access = await RequireJobsAsync(actor);
        RequireWritable();
        await using var db = await dbFactory.CreateDbContextAsync();
        var job = await db.BackupJobs.Include(j => j.Sources).FirstOrDefaultAsync(j => j.Id == jobId);
        if (job is null)
            return;
        if (!IsJobVisible(access, job))
            throw new UnauthorizedAccessException("The job is outside the actor's backup scope.");
        db.BackupJobs.Remove(job);
        await db.SaveChangesAsync();
    }

    public async Task<Guid> RunJobNowAsync(UserContext actor, Guid jobId)
    {
        var access = await RequireJobsAsync(actor);
        RequireWritable();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var job = await db.BackupJobs.AsNoTracking().Include(j => j.Sources).FirstOrDefaultAsync(j => j.Id == jobId)
                      ?? throw new ResticException("job_missing", "The backup job no longer exists.");
            if (!IsJobVisible(access, job))
                throw new UnauthorizedAccessException("The job is outside the actor's backup scope.");
            var state = await db.BackupRepositories.Where(r => r.Id == job.RepositoryId).Select(r => r.State).FirstAsync();
            if (state != BackupRepositoryState.Active)
                throw new ResticException("repository_not_active", "The repository is not active.");
        }
        return await runner.EnqueueJobAsync(jobId, BackupRunTrigger.Manual, actor.User.Id);
    }

    /// <summary>Cancels a queued/running run of a job the actor can see, or a restore the actor started.</summary>
    public async Task<bool> CancelRunAsync(UserContext actor, Guid runId)
    {
        var access = await GetAccessAsync(actor);
        var run = runner.Runs.FirstOrDefault(r => r.RunId == runId);
        if (run is null)
            return false;
        if (run.IsMaintenance)
            return access.CanManageRepositories && runner.Cancel(runId);
        await using var db = await dbFactory.CreateDbContextAsync();
        if (run.IsRestore)
        {
            // Server-folder restores are started by repository admins, who may hold no restore bit.
            var starter = await db.BackupRuns.AsNoTracking().Where(r => r.Id == runId).Select(r => r.ActorUserId).FirstOrDefaultAsync();
            return (access.CanRestore || access.CanManageRepositories)
                   && (access.RestoreUnrestricted || starter == actor.User.Id) && runner.Cancel(runId);
        }
        if (!access.CanManageJobs)
            return false;
        var job = await db.BackupJobs.AsNoTracking().Include(j => j.Sources).FirstOrDefaultAsync(j => j.Id == run.JobId);
        return job is not null && IsJobVisible(access, job) && runner.Cancel(runId);
    }
}
