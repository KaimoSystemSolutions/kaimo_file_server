using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Web.Components.Shared;
using Kaimo_File_Server.Web.Components.ViewModels;
using Microsoft.AspNetCore.Components;

namespace Kaimo_File_Server.Web.Components.Pages.CloudAccess;

// Virtual (cloud) shares tab of the Shares page. Connections are managed in
// External Storage (ConnectionAdministration); this component only lists,
// creates and edits virtual shares.
public partial class CloudAccess
{
    [Parameter] public Guid? InitialConnectionId { get; set; }
    private bool _showShareForm;
    private Guid _shareConnectionId;
    private Guid _shareDepartmentId = WellKnownGUIDs.DEPARTMENT_GLOBAL;
    private string _shareName = "";
    private string _remoteRoot = "";
    private readonly Dictionary<Guid, CloudAccessPermission> _principalLevels = [];
    private bool _busy;
    private string? _error;
    private bool _pickerOpen;
    private bool _pickerLoading;
    private string _pickerPath = "/";
    private string? _pickerError;
    private IReadOnlyList<CloudDirectoryItem> _pickerItems = [];
    private Guid _pickerConnectionId;
    private bool _pickingEditedShare;
    private CloudAccessShare? _selectedShare;
    private CloudAccessTab _activeTab = CloudAccessTab.General;
    private readonly Dictionary<Guid, CloudAccessPermission> _grantLevels = [];
    private string _editShareName = "";
    private string _editShareRoot = "";
    private Guid _editShareDepartmentId;
    private bool _showShareDepartmentConfirm;
    private bool _showShareDeleteConfirm;
    private bool _showVirtualShareDetails;

    private bool CanCreateShare => _shareConnectionId != Guid.Empty
                                   && !string.IsNullOrWhiteSpace(_shareName)
                                   && _shareDepartmentId != Guid.Empty
                                   && !string.IsNullOrWhiteSpace(_remoteRoot);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        await VM.LoadAsync();
        if (InitialConnectionId is Guid connectionId
            && VM.Connections.Any(connection => connection.Id == connectionId
                && IsReady(connection) && VM.Supports(connection, StorageProviderCapabilities.Browse)))
        {
            _shareConnectionId = connectionId;
            _showShareForm = true;
            // New shares default to the Global department; principals follow it.
            _shareDepartmentId = WellKnownGUIDs.DEPARTMENT_GLOBAL;
            await VM.LoadPrincipalsAsync(_shareDepartmentId);
        }
        StateHasChanged();
    }

    private async Task ToggleShareForm()
    {
        _showShareForm = !_showShareForm;
        _error = null;
        // Grantable principals follow the share's department (default Global).
        if (_showShareForm)
            await RunAsync(() => VM.LoadPrincipalsAsync(_shareDepartmentId));
    }

    private async Task OnShareDepartmentChangedAsync(IReadOnlyList<Guid> ids)
    {
        _shareDepartmentId = ids.FirstOrNull() ?? Guid.Empty;
        // Grantable principals change with the department, so drop any staged grants.
        _principalLevels.Clear();
        if (_shareDepartmentId != Guid.Empty)
            await RunAsync(() => VM.LoadPrincipalsAsync(_shareDepartmentId));
    }

    private void CloseShareForm() => _showShareForm = false;

    private Task ConnectionChangedAsync(ChangeEventArgs e)
    {
        // The connection no longer carries a department; grantable principals follow the
        // share's own department, loaded when that department is chosen.
        _shareConnectionId = Guid.TryParse(e.Value?.ToString(), out var id) ? id : Guid.Empty;
        _remoteRoot = "";
        return Task.CompletedTask;
    }

    private async Task CreateShareAsync() => await RunAsync(async () =>
    {
        await VM.CreateShareAsync(_shareConnectionId, _shareName, _remoteRoot, _shareDepartmentId, _principalLevels);
        _shareName = "";
        _remoteRoot = "";
        _shareDepartmentId = WellKnownGUIDs.DEPARTMENT_GLOBAL;
        _principalLevels.Clear();
        _showShareForm = false;
    });

    private async Task OpenPickerAsync()
    {
        _pickerOpen = true;
        _pickerConnectionId = _shareConnectionId;
        _pickingEditedShare = false;
        _pickerPath = "/";
        await LoadPickerAsync();
    }

    private async Task OpenEditPickerAsync()
    {
        if (_selectedShare is null) return;
        _pickerOpen = true;
        _pickerConnectionId = _selectedShare.ConnectionId;
        _pickingEditedShare = true;
        _pickerPath = _editShareRoot;
        await LoadPickerAsync();
    }

    private async Task NavigatePickerAsync(string path)
    {
        _pickerPath = path;
        await LoadPickerAsync();
    }

    private Task SelectPickerAsync(string path)
    {
        if (_pickingEditedShare) _editShareRoot = path;
        else _remoteRoot = path;
        _pickerOpen = false;
        return Task.CompletedTask;
    }

    private Task ClosePicker()
    {
        _pickerOpen = false;
        return Task.CompletedTask;
    }

    private async Task LoadPickerAsync()
    {
        _pickerLoading = true;
        _pickerError = null;
        try
        {
            _pickerItems = await VM.ListRemoteFoldersAsync(_pickerConnectionId, _pickerPath);
        }
        catch (Exception exception)
        {
            _pickerError = exception.Message;
            _pickerItems = [];
        }
        finally
        {
            _pickerLoading = false;
        }
    }

    private async Task SelectShareAsync(CloudAccessShare share)
    {
        _selectedShare = share;
        _editShareName = share.Name;
        _editShareRoot = share.RemoteRootPath;
        _editShareDepartmentId = share.DepartmentId;
        _showShareDepartmentConfirm = false;
        _showShareDeleteConfirm = false;
        _activeTab = CloudAccessTab.General;
        // Principals back the management (access) editor; loading them requires manage
        // rights. For a share the actor can only access, skip it — no management pane shows.
        if (!VM.CanManageShare(share)) return;
        await VM.LoadPrincipalsAsync(share.DepartmentId);
    }

    private async Task SwitchTabAsync(CloudAccessTab tab)
    {
        _activeTab = tab;
        if (tab == CloudAccessTab.Access && _selectedShare is not null)
        {
            var grants = await VM.LoadShareGrantsAsync(_selectedShare.Id);
            _grantLevels.Clear();
            foreach (var (principalId, level) in grants)
                _grantLevels[principalId] = level;
        }
    }

    // Users and groups are picked via the shared principal picker (its own tabs split the kinds);
    // the selection is written back into the existing HashSet so the save calls are unchanged.
    private IReadOnlyList<PrincipalPickerItem> PrincipalItems()
        => VM.AvailablePrincipals
            .Select(p => new PrincipalPickerItem(p.Id, p.Name, p is Group ? PrincipalKind.Group : PrincipalKind.User))
            .ToList();

    private async Task SaveGrantsAsync()
    {
        if (_selectedShare is null) return;
        await RunAsync(() => VM.UpdateShareGrantsAsync(_selectedShare.Id, _grantLevels));
    }

    private async Task ChangeShareDepartmentAsync()
    {
        if (_selectedShare is null) return;
        var shareId = _selectedShare.Id;
        await RunAsync(async () =>
        {
            await VM.ChangeShareDepartmentAsync(shareId, _editShareDepartmentId);
            _selectedShare = VM.Shares.SingleOrDefault(x => x.Id == shareId);
            _showShareDepartmentConfirm = false;
        });
    }

    // Name and remote folder each commit on their own button (mirroring the local share
    // settings panel). Each sends the current stored value for the other field, so an
    // uncommitted edit there is never silently applied, and only the committed field is
    // re-synced from the reloaded share.
    private async Task RenameShareAsync()
    {
        if (_selectedShare is null) return;
        var shareId = _selectedShare.Id;
        var currentRoot = _selectedShare.RemoteRootPath;
        await RunAsync(async () =>
        {
            await VM.UpdateShareAsync(shareId, _editShareName, currentRoot);
            _selectedShare = VM.Shares.SingleOrDefault(x => x.Id == shareId);
            if (_selectedShare is not null) _editShareName = _selectedShare.Name;
        });
    }

    private async Task ChangeFolderAsync()
    {
        if (_selectedShare is null) return;
        var shareId = _selectedShare.Id;
        var currentName = _selectedShare.Name;
        await RunAsync(async () =>
        {
            await VM.UpdateShareAsync(shareId, currentName, _editShareRoot);
            _selectedShare = VM.Shares.SingleOrDefault(x => x.Id == shareId);
            if (_selectedShare is not null) _editShareRoot = _selectedShare.RemoteRootPath;
        });
    }

    private async Task DeleteShareAsync(CloudAccessShare share)
    {
        await RunAsync(() => VM.DeleteShareAsync(share.Id));
        _showShareDeleteConfirm = false;
        if (_selectedShare?.Id == share.Id) _selectedShare = null;
    }

    private static bool IsReady(StorageConnection connection)
        => connection.State == StorageConnectionState.Ready;

    private void ToggleVirtualShareDetails() => _showVirtualShareDetails = !_showVirtualShareDetails;

    /// <summary>
    /// Resolves the status label shown in the share list. Reuses the local-share wording so both
    /// share kinds present identical states (available / not available / disabled). Connection
    /// readiness comes from the view model so access-only shares also report correctly.
    /// </summary>
    private string ShareStatusText(CloudAccessShare share)
    {
        if (!share.IsEnabled)
            return R("Web_ShareList_Card_Disabled");
        if (!VM.IsShareUsable(share))
            return R("Web_ShareList_Card_Unavailable");
        return R("Web_ShareList_Card_Available");
    }

    private string ConnectionDisplay(StorageConnection connection)
        => $"{connection.Name} · {VM.GetProviderName(connection.ProviderId)}";

    private void OpenShare(CloudAccessShare share) => Nav.NavigateTo($"/files/access/{share.Id}");

    private async Task RunAsync(Func<Task> action)
    {
        _busy = true;
        _error = null;
        try { await action(); }
        catch (Exception exception) { _error = exception.Message; }
        finally { _busy = false; StateHasChanged(); }
    }

    private static string R(string key) => Resources.ResourceManager.GetString(key) ?? key;

    private enum CloudAccessTab { General, Extra, Access }
}
