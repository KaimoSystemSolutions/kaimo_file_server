using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Infrastructure.Backup.Restic;
using Kaimo_File_Server.Web.Components.Shared;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Components.ViewModels.Backup;
using Kaimo_File_Server.Web.DynamicHelpers;
using Kaimo_File_Server.Web.Helpers;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Kaimo_File_Server.Web.Components.Pages.Backup.Components;

public partial class BackupRepositories
{
    // Results and runner progress are announced by the view model; the page does not
    // re-render this component on its own.
    protected override void OnInitialized() => VM.Changed += Refresh;
    private void Refresh() => _ = InvokeAsync(StateHasChanged);
    public void Dispose() => VM.Changed -= Refresh;

    [Parameter] public Guid? SelectedRepositoryId { get; set; }

    /// <summary>Opens the jobs tab; the jobs tile shows no link without it.</summary>
    [Parameter] public EventCallback OnOpenJobs { get; set; }

    private Guid? _selectedId;
    private bool _editing;
    private bool _confirmDelete;
    private bool _kitDownloaded;
    private string _kitCode = string.Empty;

    protected override void OnParametersSet()
    {
        if (SelectedRepositoryId is not null)
            _selectedId = SelectedRepositoryId;
    }

    private BackupRepositoryView? Selected => VM.Repositories.FirstOrDefault(r => r.Repository.Id == _selectedId);

    private bool CanSubmit
    {
        get
        {
            var f = VM.RepositoryForm;
            return !string.IsNullOrWhiteSpace(f.Name) && f.Backend switch
            {
                BackupBackend.Local => !string.IsNullOrWhiteSpace(f.Path),
                BackupBackend.Sftp => f.ConnectionId != Guid.Empty && !string.IsNullOrWhiteSpace(f.Path),
                BackupBackend.Rest => !string.IsNullOrWhiteSpace(f.Endpoint),
                BackupBackend.S3 => !string.IsNullOrWhiteSpace(f.Endpoint) && !string.IsNullOrWhiteSpace(f.Bucket),
                BackupBackend.Azure => !string.IsNullOrWhiteSpace(f.AzureAccountName) && !string.IsNullOrWhiteSpace(f.Bucket),
                BackupBackend.B2 or BackupBackend.Gcs or BackupBackend.Swift => !string.IsNullOrWhiteSpace(f.Bucket),
                BackupBackend.Rclone => !string.IsNullOrWhiteSpace(f.Path),
                BackupBackend.Smb => f.ConnectionId != Guid.Empty && !string.IsNullOrWhiteSpace(f.Path),
                _ => false,
            };
        }
    }

    private void Select(Guid id)
    {
        _selectedId = id;
        _editing = false;
        _confirmDelete = false;
        _kitDownloaded = false;
        _kitCode = string.Empty;
    }

    private async Task StartCreate()
    {
        VM.NewRepository();
        _editing = true;
        await VM.RefreshMirrorCandidatesAsync();
    }

    private async Task StartEdit(BackupRepositoryView view)
    {
        VM.EditRepository(view);
        _editing = true;
        _confirmDelete = false;
        await VM.RefreshMirrorCandidatesAsync();
    }

    // Any change to the target invalidates an earlier test result and may change which
    // repositories can serve as its mirror (same storage type needs the same access).
    private async Task Changed()
    {
        if (VM.RepositoryForm.Id is null)
            VM.ResetProbe();
        await VM.RefreshMirrorCandidatesAsync();
    }

    private Task BackendChanged()
    {
        VM.ApplyBackendDefaults();
        return Changed();
    }

    private async Task CreateAsync(bool connectExisting)
    {
        if (await VM.CreateRepositoryAsync(connectExisting))
        {
            Select(VM.RepositoryForm.Id!.Value);
        }
    }

    private async Task UpdateAsync()
    {
        if (await VM.UpdateRepositoryAsync())
            _editing = false;
    }

    private async Task DeleteAsync(Guid id)
    {
        if (await VM.DeleteRepositoryAsync(id))
            _selectedId = null;
        _confirmDelete = false;
    }

    private async Task DownloadKitAsync(Guid id)
    {
        var url = await VM.CreateKitDownloadUrlAsync(Nav.BaseUri, id);
        if (string.IsNullOrEmpty(url))
            return;
        _kitDownloaded = true;
        Nav.NavigateTo(url, forceLoad: true);
    }

    private async Task ConfirmKitAsync(Guid id)
    {
        if (await VM.ConfirmKitAsync(id, _kitCode))
            _kitCode = string.Empty;
    }

    private string DepartmentNames(BackupRepositoryView view)
    {
        var names = VM.AllDepartments.Where(d => view.DepartmentIds.Contains(d.Id)).Select(d => d.Name).ToList();
        return names.Count == 0 ? R("Web_Backup_Repos_DepartmentsNone") : string.Join(", ", names);
    }

    private static string Location(BackupRepository repo)
    {
        var s = ResticRepositorySettings.Parse(repo.SettingsJson);
        return repo.Backend switch
        {
            BackupBackend.Local => s.Path ?? "–",
            BackupBackend.Sftp => "sftp: " + s.Path,
            BackupBackend.Smb => "smb: " + s.Path,
            BackupBackend.Rest => s.Endpoint + (string.IsNullOrEmpty(s.Path) ? "" : "/" + s.Path.Trim('/')),
            BackupBackend.S3 => s.Endpoint + "/" + s.Bucket + (string.IsNullOrEmpty(s.Path) ? "" : "/" + s.Path.Trim('/')),
            // The URL of the remaining backends holds no secret.
            _ => ResticBackend.BuildUrl(repo.Backend, s),
        };
    }

    private static string R(string key) => BackupViewModel.R(key);
}
