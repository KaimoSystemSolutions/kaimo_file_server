using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Infrastructure.Backup.Restic;

namespace Kaimo_File_Server.Web.Components.ViewModels.Backup;

public partial class BackupViewModel
{
    // ══════════════════════════════════════════
    //  Repositories
    // ══════════════════════════════════════════

    public void NewRepository()
    {
        ClearMessages();
        ProbeResult = null;
        RepositoryForm = new BackupRepositoryForm
        {
            Path = LocalPathSuggestion,
            Departments = AllDepartments.Select(d => new CheckboxItem<Department>(d, false)).ToList(),
        };
    }

    private string LocalPathSuggestion => LocalRoots.Count > 0 ? LocalRoots[0].TrimEnd('/') + "/" : string.Empty;

    /// <summary>
    /// The path field means a local folder only for local repositories (elsewhere a prefix or
    /// remote path): the local suggestion must not silently become a bucket prefix.
    /// </summary>
    public void ApplyBackendDefaults()
    {
        var form = RepositoryForm;
        if (form.Id is not null)
            return;
        if (form.Backend == BackupBackend.Local && string.IsNullOrWhiteSpace(form.Path))
            form.Path = LocalPathSuggestion;
        else if (form.Backend != BackupBackend.Local && form.Path == LocalPathSuggestion)
            form.Path = string.Empty;
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
            AzureAccountName = settings.AzureAccountName ?? string.Empty,
            AzureEndpointSuffix = settings.AzureEndpointSuffix ?? string.Empty,
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
}
