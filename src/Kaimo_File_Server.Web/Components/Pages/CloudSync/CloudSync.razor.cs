using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components;

namespace Kaimo_File_Server.Web.Components.Pages.CloudSync;

public partial class CloudSync
{
    [Parameter] public string? ShareName { get; set; }
    [SupplyParameterFromQuery(Name = "connectedShare")]
    public Guid? ConnectedShareId { get; set; }
    [SupplyParameterFromQuery(Name = "connectedPath")]
    public string? ConnectedPath { get; set; }
    [SupplyParameterFromQuery(Name = "syncError")]
    public string? SyncError { get; set; }
    [SupplyParameterFromQuery(Name = "targetShare")]
    public Guid? TargetShareId { get; set; }
    [SupplyParameterFromQuery(Name = "targetPath")]
    public string? TargetPath { get; set; }

    private bool _initialized;
    private bool _showDeleteConfirm;
    private bool _pickerOpen;
    private bool _pickerRemote;
    private bool _pickerForNewSync;
    private bool _pickerLoading;
    private string _pickerTitle = "";
    private string _pickerPath = "";
    private string? _pickerError;
    private IReadOnlyList<CloudDirectoryItem> _pickerItems = [];

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _initialized)
            return;

        _initialized = true;
        var preferredShareId = ConnectedShareId ?? TargetShareId;
        var preferredPath = ConnectedShareId is not null ? ConnectedPath : TargetPath;
        await VM.LoadAsync(preferredShareId, preferredPath);

        if (ConnectedShareId is not null)
            Toasts.Show(Text("Web_CloudSync_Connected", "The cloud provider was connected successfully."), ToastType.Success);
        if (!string.IsNullOrWhiteSpace(SyncError))
            Toasts.Show(SyncError, ToastType.Error);

        if (ConnectedShareId is null
            && TargetShareId is not null
            && VM.SelectedSync is null
            && VM.CanCreateOnShare(TargetShareId.Value))
        {
            VM.NewShareId = TargetShareId.Value;
            VM.NewLocalPath = Infrastructure.Clouds.CloudSyncPaths.Normalize(TargetPath);
            VM.IsCreating = true;
        }

        if (ConnectedShareId is null && !string.IsNullOrWhiteSpace(ShareName))
        {
            var share = VM.Shares.FirstOrDefault(candidate => string.Equals(
                candidate.Name, ShareName, StringComparison.OrdinalIgnoreCase));
            var first = share is null ? null : VM.Syncs.FirstOrDefault(item => item.ShareId == share.Id);
            if (first is not null)
                await VM.SelectAsync(first);
        }

        StateHasChanged();
    }

    private void ToggleCreate()
    {
        VM.IsCreating = !VM.IsCreating;
        if (VM.IsCreating)
            VM.Deselect();
    }

    private void ResetNewLocalPath() => VM.NewLocalPath = "";

    private async Task SelectAsync(CloudSyncListItem sync)
    {
        _showDeleteConfirm = false;
        await VM.SelectAsync(sync);
    }

    private async Task ConnectAsync()
    {
        var uri = await VM.BuildAuthorizationUriAsync();
        if (uri is not null)
            Navigation.NavigateTo(uri, forceLoad: true);
    }

    private async Task SaveAsync()
    {
        try
        {
            if (await VM.SaveSelectedAsync())
                Toasts.Show(Text("Web_CloudSync_Saved", "Cloud sync settings saved."), ToastType.Success);
        }
        catch (Exception)
        {
            Toasts.Show(Text("Web_CloudSync_Error_Save", "The cloud sync settings could not be saved."), ToastType.Error);
        }
    }

    private async Task RunSyncAsync()
    {
        var toastId = Toasts.Show(Text("Web_CloudSync_Progress", "Synchronizing…"), ToastType.Progress);
        var success = await VM.SyncNowAsync((message, progress) =>
        {
            _ = InvokeAsync(() => Toasts.Update(toastId, message, progress, ToastType.Progress));
        });
        Toasts.Remove(toastId);
        Toasts.Show(
            success
                ? Text("Web_CloudSync_Complete", "Synchronization completed.")
                : VM.ErrorMessage ?? Text("Web_CloudSync_Error_Run", "The cloud sync failed. Check the server log."),
            success ? ToastType.Success : ToastType.Error);
    }

    private async Task DeleteAsync()
    {
        _showDeleteConfirm = false;
        if (await VM.DeleteSelectedAsync())
            Toasts.Show(Text("Web_CloudSync_Removed", "Cloud sync removed."), ToastType.Success);
    }

    private Task OpenNewLocalPicker()
    {
        _pickerForNewSync = true;
        _pickerRemote = false;
        _pickerTitle = Text("Web_CloudSync_Directory_LocalTitle", "Select local folder");
        return OpenPickerAsync(VM.NewLocalPath);
    }

    private Task OpenEditLocalPicker()
    {
        _pickerForNewSync = false;
        _pickerRemote = false;
        _pickerTitle = Text("Web_CloudSync_Directory_LocalTitle", "Select local folder");
        return OpenPickerAsync(VM.EditLocalPath);
    }

    private Task OpenRemotePicker()
    {
        _pickerForNewSync = false;
        _pickerRemote = true;
        _pickerTitle = Text("Web_CloudSync_Directory_RemoteTitle", "Select remote folder");
        return OpenPickerAsync(VM.EditRemotePath);
    }

    private async Task OpenPickerAsync(string path)
    {
        _pickerOpen = true;
        await NavigatePickerAsync(path);
    }

    private async Task NavigatePickerAsync(string path)
    {
        _pickerPath = _pickerRemote
            ? CloudSyncViewModel.NormalizeRemotePath(path)
            : Infrastructure.Clouds.CloudSyncPaths.Normalize(path);
        _pickerLoading = true;
        _pickerError = null;
        _pickerItems = [];
        StateHasChanged();

        try
        {
            if (_pickerRemote)
            {
                _pickerItems = await VM.LoadRemoteDirectoriesAsync(_pickerPath);
            }
            else
            {
                var shareId = _pickerForNewSync
                    ? VM.NewShareId
                    : VM.SelectedSync?.ShareId ?? Guid.Empty;
                _pickerItems = await VM.LoadLocalDirectoriesAsync(shareId, _pickerPath);
            }
        }
        catch (Exception)
        {
            _pickerError = Text("Web_CloudSync_Directory_Error", "This folder could not be opened.");
        }
        finally
        {
            _pickerLoading = false;
            StateHasChanged();
        }
    }

    private Task SelectPickerPathAsync(string path)
    {
        if (_pickerRemote)
            VM.EditRemotePath = CloudSyncViewModel.NormalizeRemotePath(path);
        else if (_pickerForNewSync)
            VM.NewLocalPath = Infrastructure.Clouds.CloudSyncPaths.Normalize(path);
        else
            VM.EditLocalPath = Infrastructure.Clouds.CloudSyncPaths.Normalize(path);

        ClosePicker();
        return Task.CompletedTask;
    }

    private void ClosePicker()
    {
        _pickerOpen = false;
        _pickerItems = [];
        _pickerError = null;
    }

    private static string DisplayLocalPath(string path)
        => string.IsNullOrEmpty(path) ? "/" : $"/{path}";

    private static string FormatLastSync(DateTime? value)
        => value is null
            ? Text("Web_CloudSync_Never", "Never")
            : value.Value.ToLocalTime().ToString("g");

    private static string ModeLabel(SyncMode mode) => mode switch
    {
        SyncMode.Pull => Text("Web_CloudSync_Mode_Pull", "Pull (cloud → local)"),
        SyncMode.Push => Text("Web_CloudSync_Mode_Push", "Push (local → cloud)"),
        _ => Text("Web_CloudSync_Mode_TwoWay", "Two-way")
    };

    private static string Text(string key, string fallback)
        => Resources.ResourceManager.GetString(key) ?? fallback;
}
