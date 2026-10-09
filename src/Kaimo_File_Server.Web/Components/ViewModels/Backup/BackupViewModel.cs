using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Backup.Restic;
using Kaimo_File_Server.Web.DynamicHelpers;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;

namespace Kaimo_File_Server.Web.Components.ViewModels.Backup;

/// <summary>Editable state of the repository form.</summary>
public sealed class BackupRepositoryForm
{
    public Guid? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public BackupBackend Backend { get; set; } = BackupBackend.Local;
    public string Path { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string Bucket { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public bool S3PathStyle { get; set; }
    public Guid ConnectionId { get; set; }
    public string CaCertificatePem { get; set; } = string.Empty;
    public string RestUsername { get; set; } = string.Empty;
    public string RestPassword { get; set; } = string.Empty;
    public string S3AccessKeyId { get; set; } = string.Empty;
    public string S3SecretAccessKey { get; set; } = string.Empty;
    public bool IsAppendOnly { get; set; }
    public int MinFreeSpaceGb { get; set; } = 5;
    public List<CheckboxItem<Department>> Departments { get; set; } = [];

    /// <summary>Password typed for "connect existing"; never stored in the form after use.</summary>
    public string ExistingPassword { get; set; } = string.Empty;

    public BackupRepositoryDraft ToDraft() => new(
        Name,
        Backend,
        new ResticRepositorySettings
        {
            Path = Blank(Path),
            Endpoint = Backend is BackupBackend.Rest or BackupBackend.S3 ? Blank(Endpoint) : null,
            Bucket = Backend == BackupBackend.S3 ? Blank(Bucket) : null,
            Region = Backend == BackupBackend.S3 ? Blank(Region) : null,
            S3PathStyle = Backend == BackupBackend.S3 && S3PathStyle,
            StorageConnectionId = Backend == BackupBackend.Sftp && ConnectionId != Guid.Empty ? ConnectionId : null,
            CaCertificatePem = Backend is BackupBackend.Rest or BackupBackend.S3 ? Blank(CaCertificatePem) : null,
        },
        new ResticBackendSecrets
        {
            RestUsername = Backend == BackupBackend.Rest ? Blank(RestUsername) : null,
            RestPassword = Backend == BackupBackend.Rest ? Blank(RestPassword) : null,
            S3AccessKeyId = Backend == BackupBackend.S3 ? Blank(S3AccessKeyId) : null,
            S3SecretAccessKey = Backend == BackupBackend.S3 ? Blank(S3SecretAccessKey) : null,
        },
        IsAppendOnly,
        MinFreeSpaceGb,
        Departments.Where(d => d.IsChecked).Select(d => d.Item.Id).ToList());

    private static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Editable state of the job form.</summary>
public sealed class BackupJobForm
{
    public Guid? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid RepositoryId { get; set; }
    public bool Enabled { get; set; } = true;
    public bool IncludeRecycleBin { get; set; }
    public List<CheckboxItem<ShareDefinition>> Shares { get; set; } = [];
    public List<TimeOnly> Times { get; set; } = [new(2, 0)];
    public HashSet<DayOfWeek> Days { get; set; } = [.. Enum.GetValues<DayOfWeek>()];

    public IEnumerable<Guid> SelectedShareIds => Shares.Where(s => s.IsChecked).Select(s => s.Item.Id);

    public BackupJobDraft ToDraft() => new(
        Id, Name, RepositoryId, Enabled, IncludeRecycleBin,
        SelectedShareIds.ToList(), Times.ToList(), Days.ToList());
}

/// <summary>
/// Backs the <c>/backup</c> page (restic file backup): overview, jobs, repositories and
/// activity. Every action goes through <see cref="BackupCatalogService"/>, which re-checks
/// authorization server-side.
/// </summary>
public sealed class BackupViewModel(
    AuthenticationStateProvider authState,
    IUserContextFactory userContextFactory,
    BackupCatalogService catalog,
    IBackupRunner runner,
    IDepartmentRepository departments,
    BackupDownloadTokenService downloadTokens,
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
            RepositoryChoices = await catalog.GetRepositoryChoicesAsync(_actor);
        }
        if (Access.CanManageRepositories)
        {
            Repositories = await catalog.ListRepositoriesAsync(_actor);
            AllDepartments = (await departments.GetAllAsync()).OrderByGlobalFirst().ToList();
            SshConnections = await catalog.ListSshConnectionsAsync(_actor);
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
    //  Repositories
    // ══════════════════════════════════════════

    public void NewRepository()
    {
        ClearMessages();
        ProbeResult = null;
        RepositoryForm = new BackupRepositoryForm
        {
            Path = LocalRoots.Count > 0 ? LocalRoots[0].TrimEnd('/') + "/" : string.Empty,
            Departments = AllDepartments.Select(d => new CheckboxItem<Department>(d, false)).ToList(),
        };
    }

    public void EditRepository(BackupRepositoryView view)
    {
        ClearMessages();
        ProbeResult = null;
        var r = view.Repository;
        var settings = ResticRepositorySettings.Parse(r.SettingsJson);
        RepositoryForm = new BackupRepositoryForm
        {
            Id = r.Id,
            Name = r.Name,
            Backend = r.Backend,
            Path = settings.Path ?? string.Empty,
            Endpoint = settings.Endpoint ?? string.Empty,
            Bucket = settings.Bucket ?? string.Empty,
            Region = settings.Region ?? string.Empty,
            S3PathStyle = settings.S3PathStyle,
            ConnectionId = settings.StorageConnectionId ?? Guid.Empty,
            CaCertificatePem = settings.CaCertificatePem ?? string.Empty,
            IsAppendOnly = r.IsAppendOnly,
            MinFreeSpaceGb = r.MinFreeSpaceGb,
            Departments = AllDepartments.Select(d => new CheckboxItem<Department>(d, view.DepartmentIds.Contains(d.Id))).ToList(),
        };
    }

    public void ResetProbe() => ProbeResult = null;

    public Task ProbeAsync() => RunAsync(async actor =>
    {
        ProbeResult = await catalog.ProbeAsync(actor, RepositoryForm.ToDraft());
        SuccessMessage = ProbeResult == BackupRepositoryProbe.Empty
            ? R("Web_Backup_Probe_Empty")
            : R("Web_Backup_Probe_Existing");
    });

    /// <summary>Initializes a new repository (no password) or connects an existing one.</summary>
    public Task<bool> CreateRepositoryAsync(bool connectExisting) => RunAsync(async actor =>
    {
        var password = connectExisting ? RepositoryForm.ExistingPassword : null;
        if (connectExisting && string.IsNullOrEmpty(password))
            throw new ResticException("password_required", "The repository password is required.");
        var created = await catalog.CreateRepositoryAsync(actor, RepositoryForm.ToDraft(), password);
        RepositoryForm.ExistingPassword = string.Empty;
        RepositoryForm.Id = created.Id;
        ProbeResult = null;
        await ReloadAsync();
        SuccessMessage = R(connectExisting ? "Web_Backup_Repo_Connected" : "Web_Backup_Repo_Initialized");
    });

    public Task<bool> UpdateRepositoryAsync() => RunAsync(async actor =>
    {
        await catalog.UpdateRepositoryAsync(actor, RepositoryForm.Id!.Value, RepositoryForm.ToDraft());
        RepositoryForm.RestPassword = RepositoryForm.S3SecretAccessKey = string.Empty;
        await ReloadAsync();
        SuccessMessage = R("Web_Backup_Saved");
    });

    public Task<bool> SetRepositoryEnabledAsync(Guid id, bool enabled) => RunAsync(async actor =>
    {
        await catalog.SetRepositoryEnabledAsync(actor, id, enabled);
        await ReloadAsync();
    });

    public Task<bool> DeleteRepositoryAsync(Guid id) => RunAsync(async actor =>
    {
        await catalog.DeleteRepositoryAsync(actor, id);
        await ReloadAsync();
        SuccessMessage = R("Web_Backup_Repo_Deleted");
    });

    /// <summary>Single-use download URL of the recovery kit (the controller re-checks the permission).</summary>
    public async Task<string?> CreateKitDownloadUrlAsync(string baseUri, Guid repositoryId)
    {
        if (!Access.CanManageRepositories || _actor is null || demo.ReadOnly)
            return null;
        var token = Uri.EscapeDataString(downloadTokens.Protect(
            Controllers.ResticDownloadController.KitSubjectPrefix + repositoryId, _actor.User.Id));
        return $"{baseUri.TrimEnd('/')}/api/file-backups/kit?token={token}";
    }

    public Task<bool> ConfirmKitAsync(Guid repositoryId, string code) => RunAsync(async actor =>
    {
        if (!await catalog.ConfirmRecoveryKitAsync(actor, repositoryId, code))
            throw new ResticException("kit_code_wrong", "The kit code does not match.");
        await ReloadAsync();
        SuccessMessage = R("Web_Backup_Kit_Confirmed");
    });

    // ══════════════════════════════════════════
    //  Jobs
    // ══════════════════════════════════════════

    public void NewJob()
    {
        ClearMessages();
        JobForm = new BackupJobForm
        {
            Shares = SelectableShares.Select(s => new CheckboxItem<ShareDefinition>(s, false)).ToList(),
        };
    }

    public void EditJob(BackupJobView view)
    {
        ClearMessages();
        var job = view.Job;
        var selected = view.Shares.Select(s => s.ShareId).ToHashSet();
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
        };
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
