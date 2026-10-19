using System.Globalization;
using System.Text.Json;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Infrastructure.Clouds;
using Kaimo_File_Server.Web.Components.Shared;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Kaimo_File_Server.Web.Components.Pages.ExternalStorage.Components;

// Sync definitions: list, create/edit form, run/delete actions and folder pickers.
// The weekly schedule grid lives in SyncAdministration.Schedule.cs.
public partial class SyncAdministration
{
    private enum SyncSettingsTab { General, Schedule, Advanced }

    [Parameter] public Guid? InitialConnectionId { get; set; }
    [Parameter] public Guid? InitialSyncId { get; set; }
    private bool _editing;
    private bool _busy;
    private bool _confirmDelete;
    private Guid? _editingId;
    private Guid? _selectedId;
    private string? _error;
    private ExternalStorageSyncEditModel _model = new();
    private bool _pickerOpen;
    private bool _pickerIsRemote = true;
    private bool _pickerLoading;
    private string _pickerPath = "/";
    private string? _pickerError;
    private IReadOnlyList<CloudDirectoryItem> _pickerItems = [];
    private ExternalStorageSyncListItem? SelectedItem
        => _selectedId is Guid id ? VM.Syncs.FirstOrDefault(sync => sync.Definition.Id == id) : null;
    private string PickerTitle => _pickerIsRemote
        ? R("Web_CloudAccess_FolderPickerTitle")
        : R("Web_CloudSync_Directory_LocalTitle");
    private SyncSettingsTab _activeSettingsTab = SyncSettingsTab.General;
    private SchedulePaintMode _schedulePaintMode = SchedulePaintMode.Select;
    private bool _scheduleCustomOpen;
    private ElementReference _scheduleGrid;
    private DotNetObjectReference<SyncAdministration>? _schedulePaintReference;
    private IEnumerable<StorageConnection> ReadyConnections => VM.Connections.Where(c =>
        c.State == StorageConnectionState.Ready && VM.Supports(c, StorageProviderCapabilities.Sync));
    // Live validation: the first missing/invalid field, mirroring the save-path checks.
    private string? ValidationHint
        => _model.ConnectionId == Guid.Empty ? R("Web_CloudAccess_ConnectionRequired")
            : _model.LocalShareId == Guid.Empty ? R("Web_CloudSync_Error_ShareRequired")
            : SyncNameTaken ? R("Web_ExternalStorage_SyncNameExists")
            : ExternalStorageSyncViewModel.GetFieldError(_model);
    private bool CanSave => ValidationHint is null;
    /// <summary>Live duplicate-name check against the loaded list; the view model re-checks on save.</summary>
    private bool SyncNameTaken => !string.IsNullOrWhiteSpace(_model.DisplayName)
        && !(_editingId is not null && string.Equals(SelectedItem?.Definition.DisplayName, _model.DisplayName.Trim(), StringComparison.Ordinal))
        && VM.Syncs.Any(sync => sync.Definition.Id != _editingId
            && string.Equals(sync.Definition.DisplayName?.Trim(), _model.DisplayName.Trim(), StringComparison.OrdinalIgnoreCase));
    // Local paths are stored share-relative ("" = root) but shown rooted ("/docs"),
    // exactly like remote paths. Bound on change, so normalizing never fights typing.
    private string LocalPathDisplay
    {
        get => DisplayPath(_model.LocalPath);
        set => _model.LocalPath = ShareRelativePath.Normalize(value);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await VM.LoadAsync();
            if (InitialSyncId is Guid syncId && VM.Syncs.Any(sync => sync.Definition.Id == syncId))
                _selectedId = syncId;
            else if (InitialConnectionId is Guid connectionId && ReadyConnections.Any(c => c.Id == connectionId))
                StartCreate(connectionId);
            StateHasChanged();
        }
        // Initialize once per rendered grid element (the grid is recreated when the
        // tab or edit form reopens); the JS side is idempotent, but a round trip on
        // every render floods the circuit.
        if (_scheduleGrid.Context is not null && _scheduleGrid.Id != _initializedScheduleGridId)
        {
            _initializedScheduleGridId = _scheduleGrid.Id;
            _schedulePaintReference ??= DotNetObjectReference.Create(this);
            await JS.InvokeVoidAsync("schedulePaint.initialize", _scheduleGrid, _schedulePaintReference);
        }
    }
    private string? _initializedScheduleGridId;

    private void StartCreate() => StartCreate(InitialConnectionId);
    private void StartCreate(Guid? connectionId)
    {
        _editing = true;
        _editingId = null;
        _selectedId = null;
        _error = null;
        _activeSettingsTab = SyncSettingsTab.General;
        _model = new ExternalStorageSyncEditModel { ConnectionId = connectionId ?? Guid.Empty, RemotePath = string.Empty };
        _scheduleCustomOpen = false;
        ConnectionChanged();
    }

    private void Select(ExternalStorageSyncListItem item)
    {
        _selectedId = item.Definition.Id;
        _editing = false;
        _editingId = null;
        _error = null;
        _confirmDelete = false;
    }

    // A local folder belongs to its share; start again at the share root.
    private void ShareChanged() => _model.LocalPath = string.Empty;

    private void ConnectionChanged()
    {
        // A remote folder belongs to its connection, so a new sync must pick it again.
        if (_editingId is null) _model.RemotePath = string.Empty;
        if (_model.ConnectionId != Guid.Empty
            && (!SelectedConnectionSupports(StorageProviderCapabilities.Write)
                || (SelectedConnectionSupports(StorageProviderCapabilities.OptimizedSync)
                    && _model.Mode == SyncMode.TwoWay)))
            _model.Mode = SyncMode.Pull;
    }

    private void StartEdit(ExternalStorageSyncListItem item)
    {
        var definition = item.Definition;
        _editing = true;
        _editingId = definition.Id;
        _selectedId = definition.Id;
        _error = null;
        _activeSettingsTab = SyncSettingsTab.General;
        _model = new ExternalStorageSyncEditModel
        {
            ConnectionId = definition.ConnectionId,
            LocalShareId = definition.LocalShareId,
            DisplayName = definition.DisplayName,
            Description = definition.Description,
            LocalPath = definition.LocalPath,
            RemotePath = definition.RemotePath,
            Mode = definition.Mode,
            Enabled = definition.Enabled,
            ScheduleEnabled = definition.Schedule.IsEnabled,
            IntervalSeconds = definition.Schedule.IntervalSeconds,
            ActiveScheduleSlots = new HashSet<int>((definition.Schedule.ActiveSlots ?? []).Where(CloudSyncSchedule.IsValidSlot)),
            MaxFileSizeMb = ToUnit(definition.AdvancedSettings.MaxFileSizeBytes, 1024L * 1024),
            MaxUploadRateKbps = ToUnit(definition.AdvancedSettings.MaxUploadBytesPerSecond, 1024L),
            MaxDownloadRateKbps = ToUnit(definition.AdvancedSettings.MaxDownloadBytesPerSecond, 1024L),
            ExcludedExtensions = string.Join(", ", definition.AdvancedSettings.ExcludedExtensions),
            SyncDeletions = definition.AdvancedSettings.SyncDeletions
        };
        // Only a partial week counts as custom times worth showing straight away.
        _scheduleCustomOpen = _model.ActiveScheduleSlots.Count is > 0 and < CloudSyncSchedule.SlotCount;
    }

    private async Task SaveAsync() => await ActAsync(async () =>
    {
        if (_editingId is Guid id) await VM.UpdateAsync(id, _model); else await VM.CreateAsync(_model);
        CancelEdit();
    });
    private async Task DeleteAsync(ExternalStorageSyncListItem item)
    {
        _confirmDelete = false;
        await ActAsync(() => VM.DeleteAsync(item.Definition.Id));
        if (VM.Syncs.All(sync => sync.Definition.Id != item.Definition.Id))
        {
            if (_selectedId == item.Definition.Id) _selectedId = null;
            if (_editingId == item.Definition.Id) CancelEdit();
        }
    }
    private async Task RunAsync(ExternalStorageSyncListItem item)
    {
        await ActAsync(() => VM.RunAsync(item.Definition.Id));
        if (_error is null)
            Toasts.Show(R("Web_ExternalStorage_SyncStarted"), ToastType.Success);
    }
    private async Task ActAsync(Func<Task> action)
    {
        _busy = true; _error = null;
        try { await action(); }
        catch (ArgumentException exception) { _error = exception.Message; }
        catch (InvalidOperationException exception) { _error = exception.Message; }
        catch (NotSupportedException exception) { _error = exception.Message; }
        catch { _error = R("Web_ExternalStorage_OperationFailed"); }
        finally { _busy = false; }
    }
    private void CancelEdit() { _editing = false; _editingId = null; _model = new(); }
    private async Task OpenRemotePickerAsync()
    {
        _pickerIsRemote = true;
        _pickerOpen = true;
        _pickerPath = string.IsNullOrWhiteSpace(_model.RemotePath) ? "/" : _model.RemotePath;
        await LoadPickerAsync(_pickerPath);
    }
    private async Task OpenLocalPickerAsync()
    {
        _pickerIsRemote = false;
        _pickerOpen = true;
        _pickerPath = _model.LocalPath ?? string.Empty;
        await LoadPickerAsync(_pickerPath);
    }
    private async Task NavigatePickerAsync(string path)
    {
        _pickerPath = path;
        await LoadPickerAsync(path);
    }
    private async Task LoadPickerAsync(string path)
    {
        _pickerLoading = true;
        _pickerError = null;
        try
        {
            _pickerItems = _pickerIsRemote
                ? await VM.ListRemoteDirectoriesAsync(_model.ConnectionId, _model.LocalShareId, path)
                : await VM.ListLocalDirectoriesAsync(_model.LocalShareId, path);
        }
        catch
        {
            _pickerItems = [];
            _pickerError = _pickerIsRemote
                ? R("Web_ExternalStorage_RemoteBrowseFailed")
                : R("Web_CloudSync_Directory_Error");
        }
        finally { _pickerLoading = false; }
    }
    private Task SelectPickerFolderAsync(string path)
    {
        if (_pickerIsRemote) _model.RemotePath = path;
        else _model.LocalPath = path;
        ClosePicker();
        return Task.CompletedTask;
    }
    private void ClosePicker() { _pickerOpen = false; _pickerItems = []; _pickerError = null; }
    private static long? ToUnit(long? value, long divisor) => value is > 0 ? (long)Math.Ceiling(value.Value / (double)divisor) : null;
    private static string DisplayName(ExternalStorageSyncListItem item) => string.IsNullOrWhiteSpace(item.Definition.DisplayName) ? item.ShareName : item.Definition.DisplayName;
    private static string DisplayPath(string path) => string.IsNullOrEmpty(path) ? "/" : $"/{path}";
    private string ProviderName(string id) => VM.GetProviderName(id);
    private string ProviderIdFor(ExternalStorageSyncListItem item)
        => VM.Connections.FirstOrDefault(connection => connection.Id == item.Definition.ConnectionId)?.ProviderId ?? "";
    private string ProviderFor(ExternalStorageSyncListItem item)
        => ProviderIdFor(item) is { Length: > 0 } id ? VM.GetProviderName(id) : "—";
    private bool SelectedConnectionSupports(StorageProviderCapabilities capability)
        => VM.Connections.FirstOrDefault(c => c.Id == _model.ConnectionId) is { } connection
           && VM.Supports(connection, capability);
    private static string ModeName(SyncMode mode) => mode switch { SyncMode.Pull => R("Web_CloudSync_Mode_Pull"), SyncMode.Push => R("Web_CloudSync_Mode_Push"), _ => R("Web_CloudSync_Mode_TwoWay") };
    private static string Health(ExternalStorageSyncListItem item)
        => !string.IsNullOrWhiteSpace(item.Runtime?.LastErrorCode) ? R("Web_ExternalStorage_AttentionRequired")
            : item.Runtime?.LastRunFailureCount > 0 ? R("Web_ExternalStorage_CompletedWithErrors")
            : R("Web_ExternalStorage_Healthy");
    private static string SyncState(ExternalStorageSyncListItem item)
        => item.Definition.Enabled ? Health(item) : R("Web_ExternalStorage_Disabled");
    private static StatusTone SyncStateTone(ExternalStorageSyncListItem item)
        => !item.Definition.Enabled ? StatusTone.Neutral
            : !string.IsNullOrWhiteSpace(item.Runtime?.LastErrorCode) || item.Runtime?.LastRunFailureCount > 0
                ? StatusTone.Warning
                : StatusTone.Positive;

    // The persisted failure list is capped (see SyncDefinitionRepository); the
    // total is reported separately by LastRunFailureCount. A malformed payload
    // degrades to showing just the count rather than throwing in the UI.
    private static IReadOnlyList<SyncFailure> FailedItems(ExternalStorageSyncListItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Runtime?.LastRunFailures))
            return Array.Empty<SyncFailure>();
        try
        {
            return JsonSerializer.Deserialize<List<SyncFailure>>(item.Runtime!.LastRunFailures!)
                ?? (IReadOnlyList<SyncFailure>)Array.Empty<SyncFailure>();
        }
        catch (JsonException)
        {
            return Array.Empty<SyncFailure>();
        }
    }
    private static string R(string key) => Resources.ResourceManager.GetString(key) ?? key;

    protected override void OnInitialized()
        => SyncJobRunner.OnChanged += HandleSyncJobsChanged;

    // Reload the list when a background sync finishes so the row and detail health
    // (and any new error message) reflect the outcome without a manual page reload.
    private int _lastSyncJobCount;
    private void HandleSyncJobsChanged()
    {
        int count = SyncJobRunner.Jobs.Count;
        // This page shows no live progress, so progress ticks must not re-render it
        // (while editing, every render would also re-run the schedule-grid interop).
        if (count == _lastSyncJobCount)
            return;
        bool finished = count < _lastSyncJobCount;
        _lastSyncJobCount = count;
        _ = InvokeAsync(async () =>
        {
            if (finished)
                await VM.LoadAsync();
            StateHasChanged();
        });
    }

    public async ValueTask DisposeAsync()
    {
        SyncJobRunner.OnChanged -= HandleSyncJobsChanged;
        if (_scheduleGrid.Context is not null)
        {
            try { await JS.InvokeVoidAsync("schedulePaint.dispose", _scheduleGrid); }
            catch (JSDisconnectedException) { }
        }
        _schedulePaintReference?.Dispose();
    }
}
