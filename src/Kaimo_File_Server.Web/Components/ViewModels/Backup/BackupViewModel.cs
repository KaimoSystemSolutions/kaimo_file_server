using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Backup.Restic;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;

namespace Kaimo_File_Server.Web.Components.ViewModels.Backup;

/// <summary>
/// Backs the <c>/backup</c> page (restic file backup): overview, jobs, restore, repositories
/// and activity. Every action goes through <see cref="BackupCatalogService"/>, which re-checks
/// authorization server-side.
/// </summary>
public sealed partial class BackupViewModel(
    AuthenticationStateProvider authState,
    IUserContextFactory userContextFactory,
    BackupCatalogService catalog,
    IBackupRunner runner,
    IDepartmentRepository departments,
    BackupDownloadTokenService downloadTokens,
    BackupRestoreExecutor restoreExecutor,
    DemoModeOptions demo,
    TimeProvider time,
    ILogger<BackupViewModel> logger)
{
    private UserContext? _actor;

    public bool IsLoading { get; private set; } = true;
    public bool IsBusy { get; private set; }
    public BackupAccess Access { get; private set; } = BackupAccess.None;
    public bool IsReadOnlyDemo => demo.ReadOnly;
    public string? SuccessMessage { get; private set; }
    public string? ErrorMessage { get; private set; }

    public IReadOnlyList<BackupJobView> Jobs { get; private set; } = [];
    public IReadOnlyList<BackupRepositoryView> Repositories { get; private set; } = [];
    public IReadOnlyList<BackupRun> Runs { get; private set; } = [];
    public IReadOnlyList<ShareDefinition> SelectableShares { get; private set; } = [];
    public IReadOnlyList<string> SelectablePools { get; private set; } = [];
    public BackupRepositoryChoices RepositoryChoices { get; private set; } = new([], new Dictionary<Guid, IReadOnlySet<Guid>>());
    public IReadOnlyList<Department> AllDepartments { get; private set; } = [];
    public IReadOnlyList<(Guid Id, string Name)> SshConnections { get; private set; } = [];
    public IReadOnlyList<string> LocalRoots => catalog.LocalRoots;
    public IReadOnlyList<BackupRunSnapshot> ActiveRuns => runner.Runs;

    public BackupRepositoryForm RepositoryForm { get; private set; } = new();
    public BackupRepositoryProbe? ProbeResult { get; private set; }
    public BackupJobForm JobForm { get; private set; } = new();

    /// <summary>
    /// Raised after every action. Actions run in the tab components, but the result banners
    /// live on the page, which Blazor would otherwise not re-render.
    /// </summary>
    public event Action? Changed;

    /// <summary>Lets the page fan runner progress out to the tab components.</summary>
    public void NotifyChanged() => Changed?.Invoke();

    public event Action? RunnerChanged
    {
        add => runner.OnChanged += value;
        remove => runner.OnChanged -= value;
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            _actor = await GetActorAsync();
            Access = await catalog.GetAccessAsync(_actor);
            if (!Access.CanAccessPage || _actor is null)
                return;
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load the backup page");
            ErrorMessage = R("Web_Settings_LoadFailed");
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task ReloadAsync()
    {
        if (_actor is null) return;
        Jobs = await catalog.ListJobsAsync(_actor);
        Runs = await catalog.ListRunsAsync(_actor);
        if (Access.CanManageJobs)
        {
            SelectableShares = await catalog.ListSelectableSharesAsync(_actor);
            SelectablePools = await catalog.ListSelectablePoolsAsync(_actor);
            RepositoryChoices = await catalog.GetRepositoryChoicesAsync(_actor);
        }
        if (Access.CanManageRepositories)
        {
            Repositories = await catalog.ListRepositoriesAsync(_actor);
            AllDepartments = (await departments.GetAllAsync()).OrderByGlobalFirst().ToList();
            SshConnections = await catalog.ListSshConnectionsAsync(_actor);
            ServerPoints = await catalog.ListServerBackupPointsAsync(_actor);
        }
        if (Access.CanRestore)
        {
            RestoreShares = await catalog.ListRestorableSharesAsync(_actor);
            RestoreShare = RestoreShares.FirstOrDefault(s => s.ShareId == RestoreShare?.ShareId);
        }
    }

    public void ClearMessages()
    {
        if (SuccessMessage is null && ErrorMessage is null)
            return;
        SuccessMessage = null;
        ErrorMessage = null;
        Changed?.Invoke();
    }

    // ══════════════════════════════════════════
    //  Status helpers
    // ══════════════════════════════════════════

    public DateTime? NextRunUtc(BackupJob job)
        => job.Enabled ? BackupScheduleCalculator.NextSlot(job.Schedule, time.GetUtcNow().UtcDateTime, time.LocalTimeZone) : null;

    public bool IsJobActive(Guid jobId) => runner.Runs.Any(r => r.JobId == jobId);

    /// <summary>Names of the shares stored in a pool (the shares a pool backup covers).</summary>
    public IReadOnlyList<string> SharesInPool(string pool)
        => SelectableShares.Where(s => BackupJobExecutor.IsInside(s.Path, pool))
            .Select(s => s.Name).Order(StringComparer.CurrentCultureIgnoreCase).ToList();

    public string RepositoryName(Guid id)
        => Repositories.FirstOrDefault(r => r.Repository.Id == id)?.Repository.Name
           ?? RepositoryChoices.Repositories.FirstOrDefault(r => r.Id == id)?.Name
           ?? Jobs.FirstOrDefault(j => j.Job.RepositoryId == id)?.RepositoryName
           ?? "–";

    public string JobName(Guid? id)
        => id is null ? "–" : Jobs.FirstOrDefault(j => j.Job.Id == id)?.Job.Name ?? R("Web_Backup_DeletedJob");

    /// <summary>Repositories usable for every selected share of the job form.</summary>
    public IReadOnlyList<BackupRepositoryChoice> UsableRepositoriesForForm
    {
        get
        {
            var selected = JobForm.SelectedShareIds.ToList();
            var usable = RepositoryChoices.Repositories
                .Where(r => selected.All(s => RepositoryChoices.UsableByShare.TryGetValue(s, out var set) && set.Contains(r.Id)))
                .ToList();
            // Keep an edited job's current repository visible even if it is no longer active.
            if (JobForm.RepositoryId != Guid.Empty && usable.All(r => r.Id != JobForm.RepositoryId))
                usable.Insert(0, new BackupRepositoryChoice(JobForm.RepositoryId, RepositoryName(JobForm.RepositoryId), BackupBackend.Local));
            return usable;
        }
    }

    // ══════════════════════════════════════════
    //  Jobs
    // ══════════════════════════════════════════

    public void NewJob()
    {
        ClearMessages();
        JobForm = new BackupJobForm
        {
            Shares = SelectableShares.Select(s => new CheckboxItem<ShareDefinition>(s, false)).ToList(),
            Pools = SelectablePools.Select(p => new CheckboxItem<string>(p, false)).ToList(),
        };
        JobForm.SetRetention(BackupRetention.Default());
    }

    public void EditJob(BackupJobView view)
    {
        ClearMessages();
        var job = view.Job;
        var selected = view.Shares.Select(s => s.ShareId).ToHashSet();
        var pools = job.Sources.Where(s => s.Kind == BackupSourceKind.Pool && s.PoolPath is not null).Select(s => s.PoolPath!).ToHashSet();
        JobForm = new BackupJobForm
        {
            Id = job.Id,
            Name = job.Name,
            RepositoryId = job.RepositoryId,
            Enabled = job.Enabled,
            IncludeRecycleBin = job.IncludeRecycleBin,
            Shares = SelectableShares.Select(s => new CheckboxItem<ShareDefinition>(s, selected.Contains(s.Id))).ToList(),
            Times = job.Schedule.Times.ToList(),
            Days = job.Schedule.Days.ToHashSet(),
            AlertAfterDays = job.AlertAfterDays,
            ContentLevel = job.ContentLevel,
            IncludeVersions = job.IncludeVersions,
            SourceKind = pools.Count > 0 ? BackupSourceKind.Pool : BackupSourceKind.Share,
            // A kept pool that is not mounted right now stays selectable.
            Pools = SelectablePools.Union(pools).Order(StringComparer.Ordinal)
                .Select(p => new CheckboxItem<string>(p, pools.Contains(p))).ToList(),
        };
        JobForm.SetRetention(job.Retention);
    }

    public Task<bool> SaveJobAsync() => RunAsync(async actor =>
    {
        var saved = await catalog.SaveJobAsync(actor, JobForm.ToDraft());
        JobForm.Id = saved.Id;
        await ReloadAsync();
        SuccessMessage = R("Web_Backup_Saved");
    });

    public Task<bool> DeleteJobAsync(Guid id) => RunAsync(async actor =>
    {
        await catalog.DeleteJobAsync(actor, id);
        await ReloadAsync();
        SuccessMessage = R("Web_Backup_Job_Deleted");
    });

    public Task<bool> RunNowAsync(Guid id) => RunAsync(async actor =>
    {
        await catalog.RunJobNowAsync(actor, id);
        SuccessMessage = R("Web_Backup_Job_Started");
    });

    public Task<bool> CancelRunAsync(Guid runId) => RunAsync(actor => catalog.CancelRunAsync(actor, runId));

    // ══════════════════════════════════════════
    //  Plumbing
    // ══════════════════════════════════════════

    private async Task<bool> RunAsync(Func<UserContext, Task> action)
    {
        ClearMessages();
        if (_actor is null)
        {
            ErrorMessage = R("Web_Error_NotLoggedIn");
            return false;
        }
        IsBusy = true;
        try
        {
            await action(_actor);
            return true;
        }
        catch (ResticException ex)
        {
            ErrorMessage = ErrorText(ex.Code);
        }
        catch (UnauthorizedAccessException)
        {
            ErrorMessage = R("Web_Error_NoPermission");
        }
        catch (ReadOnlyDemoException)
        {
            ErrorMessage = R("Web_Backup_Error_demo");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Backup page action failed");
            ErrorMessage = ErrorText("unexpected_error");
        }
        finally
        {
            IsBusy = false;
            Changed?.Invoke();
        }
        return false;
    }

    /// <summary>Localized text of a backup error code (falls back to the code itself).</summary>
    public static string ErrorText(string? code)
        => string.IsNullOrEmpty(code)
            ? string.Empty
            : Resources.ResourceManager.GetString("Web_Backup_Error_" + code)
              ?? string.Format(R("Web_Backup_Error_Generic"), code);

    private async Task<UserContext?> GetActorAsync()
    {
        var state = await authState.GetAuthenticationStateAsync();
        var username = state.User.Identity?.Name;
        return string.IsNullOrEmpty(username) ? null : await userContextFactory.CreateByUsernameAsync(username);
    }

    public static string R(string key) => Resources.ResourceManager.GetString(key) ?? key;
}
