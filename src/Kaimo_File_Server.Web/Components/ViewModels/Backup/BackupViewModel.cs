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
    public List<TimeOnly> MaintenanceTimes { get; set; } = [.. BackupSchedule.DefaultMaintenance().Times];
    public HashSet<DayOfWeek> MaintenanceDays { get; set; } = [.. BackupSchedule.DefaultMaintenance().Days];
    public bool ReadDataMonthly { get; set; } = true;
    public int TestRestoreSampleSize { get; set; } = 20;
    public Guid MirrorTargetId { get; set; }

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
        Departments.Where(d => d.IsChecked).Select(d => d.Item.Id).ToList(),
        new BackupSchedule { Times = MaintenanceTimes.ToList(), Days = MaintenanceDays.ToList() },
        ReadDataMonthly,
        TestRestoreSampleSize,
        MirrorTargetId == Guid.Empty ? null : MirrorTargetId);

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
    public int KeepLast { get; set; }
    public int KeepDaily { get; set; }
    public int KeepWeekly { get; set; }
    public int KeepMonthly { get; set; }
    public int KeepYearly { get; set; }
    public int AlertAfterDays { get; set; } = 3;
    public BackupContentLevel ContentLevel { get; set; } = BackupContentLevel.Files;
    public List<CheckboxItem<string>> Pools { get; set; } = [];
    public bool IncludeVersions { get; set; }

    /// <summary>
    /// A job backs up either single shares or whole pools (which contain their shares). The
    /// selection of the other kind is kept while switching but never sent.
    /// </summary>
    public BackupSourceKind SourceKind { get; set; } = BackupSourceKind.Share;

    public IEnumerable<Guid> SelectedShareIds => SourceKind == BackupSourceKind.Share
        ? Shares.Where(s => s.IsChecked).Select(s => s.Item.Id)
        : [];

    public IEnumerable<string> SelectedPools => SourceKind == BackupSourceKind.Pool
        ? Pools.Where(p => p.IsChecked).Select(p => p.Item)
        : [];

    /// <summary>A database or Full job needs no file source; a files job at least one.</summary>
    public bool HasSources => SelectedShareIds.Any() || SelectedPools.Any() || ContentLevel != BackupContentLevel.Files;

    public BackupRetention ToRetention() => new()
    {
        KeepLast = KeepLast, KeepDaily = KeepDaily, KeepWeekly = KeepWeekly, KeepMonthly = KeepMonthly, KeepYearly = KeepYearly,
    };

    public void SetRetention(BackupRetention retention)
    {
        KeepLast = retention.KeepLast;
        KeepDaily = retention.KeepDaily;
        KeepWeekly = retention.KeepWeekly;
        KeepMonthly = retention.KeepMonthly;
        KeepYearly = retention.KeepYearly;
    }

    public BackupJobDraft ToDraft() => new(
        Id, Name, RepositoryId, Enabled, IncludeRecycleBin,
        SelectedShareIds.ToList(), Times.ToList(), Days.ToList(), ToRetention(), AlertAfterDays,
        ContentLevel, SelectedPools.ToList(), IncludeVersions);
}

/// <summary>Editable state of the restore dialog.</summary>
public sealed class BackupRestoreForm
{
    public BackupRestoreMode Mode { get; set; } = BackupRestoreMode.NewFolder;
    public BackupRestoreConflict Conflict { get; set; } = BackupRestoreConflict.Rename;
    public bool RestorePermissions { get; set; }
    public Guid TargetShareId { get; set; }
    public string TargetFolder { get; set; } = string.Empty;
}

/// <summary>
/// Backs the <c>/backup</c> page (restic file backup): overview, jobs, restore, repositories
/// and activity. Every action goes through <see cref="BackupCatalogService"/>, which re-checks
/// authorization server-side.
/// </summary>
public sealed class BackupViewModel(
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
            MaintenanceTimes = r.MaintenanceSchedule.Times.ToList(),
            MaintenanceDays = r.MaintenanceSchedule.Days.ToHashSet(),
            ReadDataMonthly = r.ReadDataMonthly,
            TestRestoreSampleSize = r.TestRestoreSampleSize,
            MirrorTargetId = r.MirrorTargetId ?? Guid.Empty,
        };
    }

    /// <summary>Repositories the form's repository can mirror into; computed server-side (needs the secrets).</summary>
    public IReadOnlyList<BackupRepositoryChoice> MirrorCandidates { get; private set; } = [];

    /// <summary>Re-evaluates <see cref="MirrorCandidates"/> for the form's current backend and credentials.</summary>
    public async Task RefreshMirrorCandidatesAsync()
    {
        if (_actor is null || !Access.CanManageRepositories)
            return;
        try
        {
            MirrorCandidates = await catalog.ListMirrorCandidatesAsync(_actor, RepositoryForm.Id, RepositoryForm.ToDraft());
        }
        catch (Exception ex) when (ex is ResticException or UnauthorizedAccessException)
        {
            MirrorCandidates = [];
        }
    }

    public DateTime? NextMaintenanceUtc(BackupRepository repository)
        => repository.State == BackupRepositoryState.Active
            ? BackupScheduleCalculator.NextSlot(repository.MaintenanceSchedule, time.GetUtcNow().UtcDateTime, time.LocalTimeZone)
            : null;

    public BackupRunSnapshot? ActiveMaintenance(Guid repositoryId)
        => runner.Runs.FirstOrDefault(r => r.IsMaintenance && r.RepositoryId == repositoryId);

    public Task<bool> RunMaintenanceAsync(Guid repositoryId) => RunAsync(async actor =>
    {
        await catalog.RunMaintenanceNowAsync(actor, repositoryId);
        SuccessMessage = R("Web_Backup_Maintenance_Started");
    });

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
    //  Restore
    // ══════════════════════════════════════════

    public IReadOnlyList<BackupRestoreShare> RestoreShares { get; private set; } = [];

    /// <summary>System, pool and foreign backup points (Global repository admins only).</summary>
    public IReadOnlyList<BackupServerPointView> ServerPoints { get; private set; } = [];

    /// <summary>The selected group of <see cref="ServerPoints"/> (by <see cref="BackupServerPointView.Source"/>).</summary>
    public string? ServerSource { get; private set; }

    /// <summary>Folder below which system and pool backup points are restored.</summary>
    public string ServerRestoreRoot => restoreExecutor.ServerRestoreRoot;

    public void SelectServerSource(string source)
    {
        ClearMessages();
        RestoreShare = null;
        ServerSource = source;
    }

    public Task<bool> RestoreServerPointAsync(BackupServerPointView point) => RunAsync(async actor =>
    {
        await catalog.EnqueueServerRestoreAsync(actor, point.RepositoryId, point.SnapshotId);
        Runs = await catalog.ListRunsAsync(actor);
        SuccessMessage = R("Web_Backup_Restore_ServerStarted");
    });
    public BackupRestoreShare? RestoreShare { get; private set; }
    public IReadOnlyList<BackupPointView> BackupPoints { get; private set; } = [];
    public BackupPointView? BackupPoint { get; private set; }

    /// <summary>Share-relative folder shown in the backup-point browser ("" = share root).</summary>
    public string BrowseFolder { get; private set; } = string.Empty;
    public IReadOnlyList<BackupBrowseEntry> BrowseEntries { get; private set; } = [];
    public bool IsBrowsing { get; private set; }
    public HashSet<string> SelectedEntries { get; } = new(StringComparer.Ordinal);
    public (bool FromBackup, bool InPlace) RestoreRights { get; private set; }
    public IReadOnlyList<ShareDefinition> RestoreTargets { get; private set; } = [];
    public BackupRestoreForm RestoreForm { get; private set; } = new();

    /// <summary>Key of a backup point in the picker (snapshot ids are only unique per repository).</summary>
    public static string PointKey(BackupPointView point) => point.RepositoryId.ToString("N") + ":" + point.SnapshotId;

    /// <summary>Nothing or everything selected: the restore takes the whole folder.</summary>
    public bool RestoresWholeFolder => SelectedEntries.Count == 0 || SelectedEntries.Count == BrowseEntries.Count;

    public Task<bool> SelectRestoreShareAsync(Guid shareId) => RunAsync(async actor =>
    {
        RestoreShare = RestoreShares.FirstOrDefault(s => s.ShareId == shareId);
        ServerSource = null;
        BackupPoints = await catalog.ListBackupPointsAsync(actor, shareId);
        RestoreRights = await catalog.GetRestoreRightsAsync(actor, shareId);
        BackupPoint = BackupPoints.FirstOrDefault();
        await BrowseCoreAsync(actor, string.Empty);
    });

    public Task<bool> SelectBackupPointAsync(string key) => RunAsync(async actor =>
    {
        BackupPoint = BackupPoints.FirstOrDefault(p => PointKey(p) == key);
        // The same folder usually exists in the neighbouring backup point; fall back to the root.
        try { await BrowseCoreAsync(actor, BrowseFolder); }
        catch (ResticException) { await BrowseCoreAsync(actor, string.Empty); }
    });

    public Task<bool> OpenFolderAsync(string folder) => RunAsync(actor => BrowseCoreAsync(actor, folder));

    private async Task BrowseCoreAsync(UserContext actor, string folder)
    {
        SelectedEntries.Clear();
        BrowseEntries = [];
        BrowseFolder = folder;
        if (BackupPoint is null)
            return;
        IsBrowsing = true;
        Changed?.Invoke();
        try
        {
            BrowseEntries = await catalog.BrowseAsync(actor, BackupPoint.RepositoryId, BackupPoint.SnapshotId, folder);
        }
        finally
        {
            IsBrowsing = false;
        }
    }

    public void ToggleEntry(string name)
    {
        if (!SelectedEntries.Remove(name))
            SelectedEntries.Add(name);
    }

    public void SelectAllEntries(bool selected)
    {
        SelectedEntries.Clear();
        if (selected)
            SelectedEntries.UnionWith(BrowseEntries.Select(e => e.Name));
    }

    /// <summary>Resets the dialog to the least invasive mode the actor may use.</summary>
    public Task<bool> PrepareRestoreAsync() => RunAsync(async actor =>
    {
        // "Another share" never offers the share being restored (that is "new folder").
        RestoreTargets = (await catalog.ListRestoreTargetSharesAsync(actor)).Where(s => s.Id != RestoreShare?.ShareId).ToList();
        RestoreForm = new BackupRestoreForm
        {
            Mode = RestoreRights.FromBackup ? BackupRestoreMode.NewFolder : BackupRestoreMode.InPlace,
            TargetShareId = RestoreTargets.FirstOrDefault()?.Id ?? Guid.Empty,
        };
    });

    /// <summary>RestoreBackupInPlace on the destination is needed to write permissions back.</summary>
    public bool CanRestorePermissions => RestoreForm.Mode == BackupRestoreMode.OtherShare
        ? RestoreForm.TargetShareId != Guid.Empty
        : RestoreRights.InPlace;

    public Task<bool> StartRestoreAsync() => RunAsync(async actor =>
    {
        if (RestoreShare is null || BackupPoint is null)
            return;
        var form = RestoreForm;
        await catalog.EnqueueRestoreAsync(actor, new BackupRestoreRequest(
            BackupPoint.RepositoryId,
            BackupPoint.SnapshotId,
            RestoreShare.ShareId,
            BrowseFolder,
            RestoresWholeFolder ? [] : SelectedEntries.ToList(),
            form.Mode,
            form.Conflict,
            form.RestorePermissions && CanRestorePermissions,
            form.Mode == BackupRestoreMode.OtherShare ? form.TargetShareId : null,
            form.Mode == BackupRestoreMode.OtherShare ? form.TargetFolder : null));
        Runs = await catalog.ListRunsAsync(actor);
        SuccessMessage = R("Web_Backup_Restore_Started");
    });

    /// <summary>Single-use URL streaming one entry of the current folder (file raw, folder as ZIP).</summary>
    public string? CreateEntryDownloadUrl(string baseUri, string name)
    {
        if (_actor is null || demo.ReadOnly || !RestoreRights.FromBackup || BackupPoint is null)
            return null;
        var subject = Controllers.ResticDownloadController.DownloadSubject(
            BackupPoint.RepositoryId, BackupPoint.SnapshotId, Kaimo_File_Server.Core.Helpers.ShareRelativePath.Combine(BrowseFolder, name));
        return $"{baseUri.TrimEnd('/')}/api/file-backups/download?token={Uri.EscapeDataString(downloadTokens.Protect(subject, _actor.User.Id))}";
    }

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
