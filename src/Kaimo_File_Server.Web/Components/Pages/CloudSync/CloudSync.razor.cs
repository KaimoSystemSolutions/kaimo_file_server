using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using System.Globalization;

namespace Kaimo_File_Server.Web.Components.Pages.CloudSync;

/// <summary>
/// UI orchestration for the provider-neutral cloud-sync workspace. Business
/// authorization and persistence remain in <see cref="CloudSyncViewModel"/>.
/// </summary>
public partial class CloudSync : IAsyncDisposable
{
    private enum SchedulePaintMode
    {
        Select,
        Deselect
    }

    private enum CloudSyncSettingsTab
    {
        General,
        Schedule,
        Advanced
    }

    private static readonly DayOfWeek[] ScheduleDays =
    [
        DayOfWeek.Monday,
        DayOfWeek.Tuesday,
        DayOfWeek.Wednesday,
        DayOfWeek.Thursday,
        DayOfWeek.Friday,
        DayOfWeek.Saturday,
        DayOfWeek.Sunday
    ];

    [Parameter] public string? ShareName { get; set; }
    [SupplyParameterFromQuery(Name = "connectedShare")]
    public Guid? ConnectedShareId { get; set; }
    [SupplyParameterFromQuery(Name = "connectedPath")]
    public string? ConnectedPath { get; set; }
    [SupplyParameterFromQuery(Name = "syncError")]
    public string? SyncError { get; set; }
    [SupplyParameterFromQuery(Name = "remoteFolderRequired")]
    public bool RemoteFolderRequired { get; set; }
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
    private SchedulePaintMode _schedulePaintMode = SchedulePaintMode.Select;
    private CloudSyncSettingsTab _activeSettingsTab = CloudSyncSettingsTab.General;
    private ElementReference _scheduleGrid;
    private DotNetObjectReference<CloudSync>? _schedulePaintReference;

    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private DateFormatService DateFmt { get; set; } = default!;

    /// <summary>
    /// Performs the initial authenticated load after interactive rendering and
    /// restores selection or create state supplied through navigation parameters.
    /// </summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_initialized)
        {
            _initialized = true;
            var preferredShareId = ConnectedShareId ?? TargetShareId;
            var preferredPath = ConnectedShareId is not null ? ConnectedPath : TargetPath;
            await VM.LoadAsync(preferredShareId, preferredPath);

            if (ConnectedShareId is not null)
            {
                Toasts.Show(Text("Web_CloudSync_Connected", "The cloud provider was connected successfully."), ToastType.Success);
                if (RemoteFolderRequired && VM.RequiresRemoteFolderSelection)
                {
                    Toasts.Show(
                        Text("Web_CloudSync_RemoteFolder_Required", "Select a remote folder before saving this cloud sync. The root folder is allowed."),
                        ToastType.Info);
                    await OpenRemotePicker();
                }
            }
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

        if (_scheduleGrid.Context is not null)
        {
            _schedulePaintReference ??= DotNetObjectReference.Create(this);
            await JS.InvokeVoidAsync("schedulePaint.initialize", _scheduleGrid, _schedulePaintReference);
        }
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

    /// <summary>Starts the selected provider's authorization flow after validation.</summary>
    private async Task ConnectAsync()
    {
        var uri = await VM.BuildAuthorizationUriAsync();
        if (uri is not null)
            Navigation.NavigateTo(uri, forceLoad: true);
    }

    /// <summary>Saves the selected mapping and translates failures into a user notification.</summary>
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

    /// <summary>
    /// Starts the selected sync as a tracked UI job. The job handle owns the
    /// cancellation token shared by the global menu and progress toast, while the
    /// view model performs authorization, persistence, and provider interaction.
    /// </summary>
    private async Task RunSyncAsync()
    {
        if (VM.SelectedSync is null)
            return;

        var selected = VM.SelectedSync;
        var initialMessage = Text("Web_CloudSync_Progress", "Synchronizing…");
        using var job = Jobs.Start(
            string.Format(
                Text("Web_Jobs_CloudSync_Title", "Cloud sync: {0}"),
                selected.ShareName),
            $"{DisplayLocalPath(selected.LocalPath)} ↔ {VM.EditRemotePath}",
            "cloud-sync");

        // Closing this progress toast is an explicit cancellation action. The
        // toast container removes the visual only after invoking this callback.
        var toastId = Toasts.Show(
            initialMessage,
            ToastType.Progress,
            onDismiss: () =>
            {
                job.Cancel();
                return Task.CompletedTask;
            });
        // Keep both progress surfaces consistent without coupling the provider
        // abstraction to Razor or the toast implementation.
        var success = await VM.SyncNowAsync((message, progress) =>
        {
            job.Update(message ?? initialMessage, progress);
            _ = InvokeAsync(() => Toasts.Update(toastId, message, progress, ToastType.Progress));
        }, job.CancellationToken);

        job.Complete();
        Toasts.Remove(toastId);
        Toasts.Show(
            success
                ? Text("Web_CloudSync_Complete", "Synchronization completed.")
                : VM.ErrorMessage ?? Text("Web_CloudSync_Error_Run", "The cloud sync failed. Check the server log."),
            success
                ? ToastType.Success
                : VM.LastSyncWasCancelled ? ToastType.Info : ToastType.Error);
    }

    /// <summary>Confirms removal through the view model and reports successful completion.</summary>
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

    /// <summary>Opens the shared directory dialog and loads its initial path.</summary>
    private async Task OpenPickerAsync(string path)
    {
        _pickerOpen = true;
        await NavigatePickerAsync(path);
    }

    /// <summary>
    /// Normalizes and loads one picker level from either the local file service
    /// or the selected cloud provider while maintaining explicit error state.
    /// </summary>
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

    /// <summary>Writes the chosen path to the correct create/edit field and closes the dialog.</summary>
    private Task SelectPickerPathAsync(string path)
    {
        if (_pickerRemote)
        {
            VM.EditRemotePath = CloudSyncViewModel.NormalizeRemotePath(path);
            VM.ConfirmRemoteFolderSelection();
        }
        else if (_pickerForNewSync)
            VM.NewLocalPath = Infrastructure.Clouds.CloudSyncPaths.Normalize(path);
        else
            VM.EditLocalPath = Infrastructure.Clouds.CloudSyncPaths.Normalize(path);

        ClosePicker();
        return Task.CompletedTask;
    }

    /// <summary>Clears transient picker state so stale results are never rendered on reopen.</summary>
    private void ClosePicker()
    {
        _pickerOpen = false;
        _pickerItems = [];
        _pickerError = null;
    }

    private static string DisplayLocalPath(string path)
        => string.IsNullOrEmpty(path) ? "/" : $"/{path}";

    private string FormatLastSync(DateTime? value)
        => DateFmt.FormatDateTime(value, nullText: Text("Web_CloudSync_Never", "Never"));

    private static string ModeLabel(SyncMode mode) => mode switch
    {
        SyncMode.Pull => Text("Web_CloudSync_Mode_Pull", "Pull (cloud → local)"),
        SyncMode.Push => Text("Web_CloudSync_Mode_Push", "Push (local → cloud)"),
        _ => Text("Web_CloudSync_Mode_TwoWay", "Two-way")
    };

    private static string ScheduleDayName(DayOfWeek day)
        => CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(day);

    private string SchedulePaintModeClass => _schedulePaintMode == SchedulePaintMode.Select
        ? "cloud-schedule-scroll--paint-select"
        : "cloud-schedule-scroll--paint-deselect";

    private bool SchedulePaintValue => _schedulePaintMode == SchedulePaintMode.Select;

    private string SchedulePaintModeLabel()
        => _schedulePaintMode == SchedulePaintMode.Select
            ? Text("Web_CloudSync_Schedule_Paint_Select", "Select")
            : Text("Web_CloudSync_Schedule_Paint_Deselect", "Deselect");

    private void PaintScheduleSlot(DayOfWeek day, int hour)
        => VM.SetScheduleSlot(day, hour, SchedulePaintValue);

    [JSInvokable]
    public Task PaintScheduleRectangleFromJs(int startRow, int startHour, int endRow, int endHour)
    {
        if (!VM.CanConfigureSelected
            || !VM.EditScheduleEnabled
            || startRow < 0
            || endRow < 0
            || startRow >= ScheduleDays.Length
            || endRow >= ScheduleDays.Length
            || startHour is < 0 or >= CloudSyncSchedule.HoursPerDay
            || endHour is < 0 or >= CloudSyncSchedule.HoursPerDay)
            return Task.CompletedTask;

        int firstRow = Math.Min(startRow, endRow);
        int lastRow = Math.Max(startRow, endRow);
        int firstHour = Math.Min(startHour, endHour);
        int lastHour = Math.Max(startHour, endHour);

        for (int row = firstRow; row <= lastRow; row++)
        {
            for (int hour = firstHour; hour <= lastHour; hour++)
                VM.SetScheduleSlot(ScheduleDays[row], hour, SchedulePaintValue);
        }

        return InvokeAsync(StateHasChanged);
    }

    private void PaintScheduleDay(DayOfWeek day)
        => VM.SetScheduleDay(day, SchedulePaintValue);

    private void PaintScheduleHour(int hour)
        => VM.SetScheduleHour(hour, SchedulePaintValue);

    private string ScheduleDayActionLabel(DayOfWeek day)
        => string.Format(
            Text("Web_CloudSync_Schedule_Day_Paint", "Every hour on {0}: {1}"),
            ScheduleDayName(day),
            SchedulePaintModeLabel());

    private string ScheduleHourActionLabel(int hour)
        => string.Format(
            Text("Web_CloudSync_Schedule_Hour_Paint", "{0}:00 on every day: {1}"),
            hour.ToString("00"),
            SchedulePaintModeLabel());

    private static string ScheduleSlotLabel(DayOfWeek day, int hour, bool active)
        => string.Format(
            Text(
                active
                    ? "Web_CloudSync_Schedule_Slot_On"
                    : "Web_CloudSync_Schedule_Slot_Off",
                active
                    ? "{0}, {1}:00–{2}:00: active"
                    : "{0}, {1}:00–{2}:00: inactive"),
            ScheduleDayName(day),
            hour.ToString("00"),
            ((hour + 1) % CloudSyncSchedule.HoursPerDay).ToString("00"));

    private static string Text(string key, string fallback)
        => Resources.ResourceManager.GetString(key) ?? fallback;

    public async ValueTask DisposeAsync()
    {
        if (_scheduleGrid.Context is not null)
        {
            try
            {
                await JS.InvokeVoidAsync("schedulePaint.dispose", _scheduleGrid);
            }
            catch (JSDisconnectedException)
            {
                // The circuit is already gone; browser-side listeners disappear with it.
            }
        }

        _schedulePaintReference?.Dispose();
    }
}
