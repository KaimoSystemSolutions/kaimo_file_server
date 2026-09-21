
using System.Collections;
using System.Diagnostics;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Web.Components.Pages.Files.FileBrowser.components;
using Kaimo_File_Server.Web.Components.Shared;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.DynamicHelpers;
using Kaimo_File_Server.Web.Helpers;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Kaimo_File_Server.Web.Components.Pages.Files.FileBrowser;

public partial class FileBrowser
{
// ========== Lifecycle ==========

    public async Task Refresh()
        => await OnParametersSetAsync();

    protected override async Task OnParametersSetAsync()
    {
        _showFolderAcl = false;
        _aclFolderPath = "";
        _selectedItems.Clear();
        _sortColumn = null;
        _sortDirection = 0;
        _showCreateFolder = false;

        _contextMenuComponent?.CloseContextMenu();

        await VM.LoadShareAsync(ShareName, EffectiveLoadSubPath);
        UpdateAclPath();
        await LoadAclCounts();
        VM.OnStateChanged -= OnVmStateChanged;
        VM.OnStateChanged += OnVmStateChanged;

        TryApplyPendingFileSelection();

        await CheckForJustSynced();
    }

    private async Task CheckForJustSynced()
    {
        if (!VM.Capabilities.HasCloudSync || string.IsNullOrEmpty(JustSynced))
            return;
        
        var match = VM.Items.FirstOrDefault(i => i.IsDirectory && i.Name == JustSynced);

        if (match is not null)
            await OpenSyncDialog(match);
    }

    private void OnFileSelectionRequested()
    {
        if (TryApplyPendingFileSelection())
            _ = InvokeAsync(StateHasChanged);
    }

    private bool TryApplyPendingFileSelection()
    {
        if (!VM.Capabilities.HasSearchIntegration)
            return false;

        if (!FileSelectionCoordinator.TryConsume(ShareName, SubPath ?? "", out var itemName))
            return false;

        _selectedItems.Clear();
        var item = FindShareItem(itemName);
        if (item is not null)
        {
            _selectedItems.Add(item);
            // Scroll the (possibly off-screen, virtualized) row into view after the
            // next render, so a search hit is always visible, not just selected.
            _pendingScrollIndex = SortedEntries.IndexOf(item);
        }

        return true;
    }

    // Index of a search-selected entry to scroll to on the next render (-1 = none).
    private int _pendingScrollIndex = -1;

    private void OnVmStateChanged() => InvokeAsync(StateHasChanged);

    public void Dispose()
    {
        VM.OnStateChanged -= OnVmStateChanged;
        FileSelectionCoordinator.SelectionRequested -= OnFileSelectionRequested;
        _ = JS.InvokeVoidAsync("fileMarquee.dispose", "#file-selection-area");
        _dotNetRef?.Dispose();
        UploadCoordinator.OnFilesSelected -= OnFileUploaded;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_jsInitialized && _fileDropZone.Id is not null)
        {
            _jsInitialized = true;
            _dotNetRef = DotNetObjectReference.Create(this);
            if (VM.Capabilities.CanUpload)
                await JS.InvokeVoidAsync("initFileUpload", "#file-drop-zone");
            await JS.InvokeVoidAsync("fileMarquee.initialize", "#file-selection-area", _dotNetRef);
            if (VM.Capabilities.CanMove)
                await JS.InvokeVoidAsync("initInternalDragDrop");
        }

        if (_pendingScrollIndex >= 0)
        {
            var index = _pendingScrollIndex;
            _pendingScrollIndex = -1;
            await JS.InvokeVoidAsync(
                "fileMarquee.scrollToIndex", "#file-selection-area",
                index, ItemHeightPx, VM.HasParent);
        }
    }

    private void UpdateAclPath() => _aclPath = VM.CurrentPath ?? "";

    private void ToggleAclPanel()
    {
        _showAclPanel = !_showAclPanel;
        _showFolderAcl = false;
    }

    // ========== Create folder ==========

    internal void ShowCreateFolderDialog()
    {
        _newFolderName = "";
        _showCreateFolder = true;
    }


    private void CancelCreateFolder() => _showCreateFolder = false;

    private async Task OnCreateFolderKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && !string.IsNullOrWhiteSpace(_newFolderName))
            await ConfirmCreateFolder();
        else if (e.Key == "Escape")
            CancelCreateFolder();
    }

    private async Task ConfirmCreateFolder()
    {
        if (string.IsNullOrWhiteSpace(_newFolderName)) return;
        if (LiveNameError(_newFolderName, null) is not null) return;
        var folderName = _newFolderName.Trim();

        _showCreateFolder = false;
        await RenderClosedDialogAsync();

        var toastId = Toast.Show(Resources.Web_Common_Creating, ToastType.Progress);
        var result = await VM.CreateFolderAsync(folderName);
        if (result.Success)
        {
            Toast.Update(
                toastId,
                Resources.Web_Folder_Created,
                type: ToastType.Success);
            await VM.RefreshCurrentDirectoryAsync();
        }
        else
        {
            Toast.Update(
                toastId,
                result.Error ?? Resources.Web_Error_CreateFolderFailed,
                type: ToastType.Error);
        }
    }

    // ========== Key Input detection ==========
    
    private async Task OnKeyDownItemList(KeyboardEventArgs e)
    {
        if (e.Key == "Delete" && VM.Capabilities.CanDelete && _selectedItems.Count > 0)
            await DeleteSelected();

        if (e.Key == "F2" && VM.Capabilities.CanRename && _selectedItems.Count == 1)
            await RenameSelected();
        
        // copy/cut/paste
        if (e.CtrlKey)
        {
            if (e.Key == "c" && VM.Capabilities.CanCopy)
                await PutIntoClipboard(false);
        
            // A virtual share can be writable for provider-native operations, but
            // it is never a cut source. Cross-share transfers must preserve the
            // remote original and therefore start as copies.
            if (e.Key == "x" && VM.Capabilities.CanCopy && VM.Capabilities.CanCut)
                await PutIntoClipboard(true);

            if (e.Key == "v" && VM.Capabilities.CanCopy)
                await PasteClipboard();
        }
        
        
    }
    
    

    
    // ========== Delete ==========
    
    private async Task OnDeleteKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter")
            await ConfirmDelete();
        else if (e.Key == "Escape")
            CancelDelete();
    }

    private void RequestDelete(FileMetadata item)
    {
        _deleteTargets = new List<FileMetadata> { item };
        _deleteError = null;
        _showDeleteConfirm = true;
    }

    private void CancelDelete()
    {
        _showDeleteConfirm = false;
        _deleteTargets.Clear();
    }

    private async Task ConfirmDelete()
    {
        if (!_deleteTargets.Any()) return;

        var targets = _deleteTargets.ToList();
        var deletedNames = new List<string>();

        _showDeleteConfirm = false;
        _deleteTargets.Clear();
        _deleteError = null;
        _selectedItems.Clear();
        await RenderClosedDialogAsync();

        var toastId = Toast.Show(
            string.Format(T("Web_Delete_Progress"), 0, targets.Count),
            ToastType.Progress);

        // A large delete can take a while, so surface it in the global job menu
        // alongside the toast. The job mirrors the upload/transfer pattern: one job
        // per batch, per-item progress, cancellable from the menu or toast.
        var jobTitle = string.Format(
            T("Web_Jobs_Delete_Title"),
            targets.Count == 1 ? targets[0].Name : string.Format(T("Web_Transfer_Items"), targets.Count));
        using var job = Jobs.Start(jobTitle, Resources.Web_Common_Deleting, "delete");
        Toast.Update(toastId, onDismiss: () => { job.Cancel(); return Task.CompletedTask; });

        var done = 0;
        foreach (var target in targets)
        {
            if (job.CancellationToken.IsCancellationRequested)
                break;

            var result = await VM.DeleteAsync(target);
            if (!result.Success)
            {
                Toast.Update(
                    toastId,
                    result.Error ?? Resources.Web_Error_DeleteFailed,
                    type: ToastType.Error);
                await VM.LoadShareAsync(ShareName, SubPath ?? "");
                return;
            }

            deletedNames.Add(target.Name);
            done++;
            var percent = targets.Count == 0 ? 100 : done * 100 / targets.Count;
            job.Update(string.Format(T("Web_Jobs_ItemCounter"), done, targets.Count), percent);
            Toast.Update(
                toastId,
                string.Format(T("Web_Delete_Progress"), done, targets.Count),
                progress: percent);
        }

        Toast.Update(
            toastId,
            deletedNames.Count == 1
                ? string.Format(Resources.Web_Delete_Success, deletedNames[0])
                : string.Format(Resources.Web_Delete_BatchSuccess, deletedNames.Count),
            type: ToastType.Success);
        await VM.LoadShareAsync(ShareName, SubPath ?? "");
    }

    /// <summary>
    /// Blazor normally renders after the complete event handler returns. Explicitly
    /// render a dialog close before starting filesystem and refresh work.
    /// </summary>
    private async Task RenderClosedDialogAsync()
    {
        await InvokeAsync(StateHasChanged);
        // An actually incomplete await lets Blazor flush the render batch to the
        // browser before a following operation happens to complete synchronously.
        await Task.Delay(1);
    }

    // ========== Rename ==========

    private void CancelRename()
    {
        _showRenameDialog = false;
        _renameTarget = null;
    }

    private async Task OnRenameKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && !string.IsNullOrWhiteSpace(_renameNewName))
            await ConfirmRename();
        else if (e.Key == "Escape")
            CancelRename();
    }

    private async Task ConfirmRename()
    {
        if (_renameTarget is null || string.IsNullOrWhiteSpace(_renameNewName)) return;
        if (LiveNameError(_renameNewName, _renameTarget) is not null) return;

        var target = _renameTarget;
        var newName = _renameNewName.Trim();

        _selectedItems.Clear();
        _showRenameDialog = false;
        _renameTarget = null;
        await RenderClosedDialogAsync();

        var toastId = Toast.Show(Resources.Web_Common_Renaming, ToastType.Progress);
        var result = await VM.RenameAsync(target, newName);
        if (!result.Success)
        {
            Toast.Update(toastId, result.Error ?? Resources.Web_Error_RenameFailed, type: ToastType.Error);
            await VM.LoadShareAsync(ShareName, SubPath ?? "");
            return;
        }

        Toast.Update(toastId, Resources.Web_Rename_Success, type: ToastType.Success);
        await VM.LoadShareAsync(ShareName, SubPath ?? "");
        await LoadAclCounts();

        // put renamed item into the new selection
        var newItem = FindShareItem(newName);
        if (newItem is not null)
            _selectedItems.Add(newItem);
    }

    /// <summary>
    /// Live validation for the create-folder and rename dialogs. Returns a
    /// localized error message, or <c>null</c> when the name is acceptable.
    /// An empty box is treated as "not yet invalid" so the user is not nagged
    /// before typing anything.
    /// </summary>
    private string? LiveNameError(string? name, FileMetadata? exclude)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var trimmed = name.Trim();

        var errors = WindowsFileNameHelper.GetValidationErrors(trimmed);
        if (errors.Count > 0)
            return errors[0];

        var exists = VM.Items.Any(i =>
            i.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase)
            && (exclude is null || !i.Path.Equals(exclude.Path, StringComparison.OrdinalIgnoreCase)));

        return exists ? Resources.Web_Error_ItemExists : null;
    }

    public void TriggerReRender()
        => StateHasChanged();

    // ========== Versioning ==========

    /// <summary>Opens the version-history dialog for a file or the point-in-time dialog for a folder.</summary>
    public async Task OpenVersionDialog(FileMetadata item)
        => await _versionDialogComponent.Open(item);

    /// <summary>Opens the properties dialog (metadata + optional ACL tab) for a file or folder.</summary>
    public async Task OpenPropertiesDialog(FileMetadata item)
        => await _propertiesDialogComponent.Open(item);

    /// <summary>Opens the "create hash" dialog for a single file.</summary>
    public async Task OpenHashDialog(FileMetadata item)
        => await _hashDialogComponent.Open(item);

    public async Task OpenShareLinkDialog(FileMetadata item)
        => await _shareLinkDialogComponent.Open(item);

    /// <summary>After a restore the live file changed — reload the listing.</summary>
    private async Task OnVersionRestored()
    {
        await VM.LoadShareAsync(ShareName, SubPath ?? "");
        StateHasChanged();

    }

    private FileMetadata? FindShareItem(string itemName)
    {
        FileMetadata? file = SortedFiles
            .FirstOrDefault(file => file.Name.Equals(itemName));

        if (file is not null)
            return file;

        return SortedDirectories
            .FirstOrDefault(folder => folder.Name.Equals(itemName));
    }

    // ========== ACL Helpers ==========

    private int GetAclCount(string path) => VM.GetAclCount(path);
    private async Task LoadAclCounts() => await VM.LoadAclCountsAsync();

    private string GetDirAclPath(FileMetadata dir)
        => VM.ShareRelativeOf(dir);

    /// <summary>Emblem icon for a sync's transfer direction.</summary>
    private static string SyncIcon(Core.Services.DataServices.SyncMode mode) => mode switch
    {
        Core.Services.DataServices.SyncMode.Pull => "sync-pull.svg",
        Core.Services.DataServices.SyncMode.Push => "sync-push.svg",
        _ => "sync-twoway.svg"
    };

    /// <summary>Emblem icon: synced entries keep the direction glyph; unsynced ones warn.</summary>
    private static string SyncMarkerIcon(SyncFolderMarker marker) => marker.State switch
    {
        SyncItemState.PendingUpload => "upload.svg",   // will still be uploaded
        SyncItemState.PullBlocked => "warning.svg",    // pull-only: never uploaded
        _ => SyncIcon(marker.Mode)
    };

    /// <summary>Info tint for a synced entry, warning tint for anything still pending.</summary>
    private static string SyncMarkerColor(SyncItemState state)
        => state == SyncItemState.Synced ? "var(--info)" : "var(--warning)";

    private static string SyncIndicatorClass(SyncItemState state)
        => state == SyncItemState.Synced ? "" : "sync-indicator--warn";

    private string SyncMarkerTooltip(SyncFolderMarker marker) => marker.State switch
    {
        SyncItemState.PendingUpload => string.Format(Resources.Web_Sync_PendingUploadTooltip, marker.SyncName),
        SyncItemState.PullBlocked => string.Format(Resources.Web_Sync_PullBlockedTooltip, marker.SyncName),
        _ => string.Format(Resources.Web_Sync_FolderTooltip, marker.SyncName)
    };

    private string GetFileAclPath(FileMetadata file)
        => VM.ShareRelativeOf(file);

    internal void OpenFolderAcl(FileMetadata dir)
    {
        var path = GetDirAclPath(dir);
        if (_showFolderAcl && _aclFolderPath == path)
        {
            _showFolderAcl = false;
            _aclFolderPath = "";
        }
        else
        {
            _showFolderAcl = true;
            _aclFolderPath = path;
        }
    }

    internal void OpenFileAcl(FileMetadata file)
    {
        var path = GetFileAclPath(file);
        if (_showFolderAcl && _aclFolderPath == path)
        {
            _showFolderAcl = false;
            _aclFolderPath = "";
        }
        else
        {
            _showFolderAcl = true;
            _aclFolderPath = path;
        }
    }

    private void CloseFolderAcl()
    {
        _showFolderAcl = false;
        _aclFolderPath = "";
    }

    // ========== Navigation ==========

    /// <summary>Double-click / open: navigate into a folder or preview a file.</summary>
    private async Task OnItemActivate(FileMetadata entry)
    {
        if (entry.IsDirectory)
            NavigateTo(entry);
        else
            await _filePreviewComponent.OpenFilePreview(entry);
    }

    /// <summary>Open the inline ACL editor for a directory or a file.</summary>
    private void OpenEntryAcl(FileMetadata entry)
    {
        if (entry.IsDirectory)
            OpenFolderAcl(entry);
        else
            OpenFileAcl(entry);
    }

    internal void NavigateTo(FileMetadata dir)
        => NavigateToRoutePath(VM.RouteSubPathOf(VM.ShareRelativeOf(dir)));

    private void NavigateUp()
        => NavigateToRoutePath(VM.RouteSubPathOf(VM.ParentPath ?? ""));

    // Navigate to a route-relative sub-path either by routing (default) or, when
    // InPlaceNavigation is set, by reloading the listing without changing the URL — so a
    // public share link never grows a folder name after its token.
    internal void NavigateToRoutePath(string routeSubPath)
    {
        if (InPlaceNavigation)
        {
            _ = NavigateInPlaceAsync(routeSubPath);
            return;
        }

        Nav.NavigateTo(string.IsNullOrEmpty(routeSubPath)
            ? CurrentShareRoute
            : $"{CurrentShareRoute}/{routeSubPath}");
    }

    private async Task NavigateInPlaceAsync(string routeSubPath)
    {
        _inPlaceSubPath = routeSubPath;
        _selectedItems.Clear();
        _sortColumn = null;
        _sortDirection = 0;
        _contextMenuComponent?.CloseContextMenu();

        await VM.LoadShareAsync(ShareName, routeSubPath);
        UpdateAclPath();
        StateHasChanged();
    }

    /// <summary>
    /// Scoped ACL-management right for the current share, resolved server-side by
    /// the view model (ManagementPermission.ManageShareAcls on this share). Gates the
    /// ACL panel, badges and permission actions — and the context-menu entry.
    /// </summary>
    public bool CanManageAcls()
        => VM.Capabilities.HasFileAcls
           && !VM.IsLoading
           && VM.ErrorMessage is null
           && VM.CanManageAcls;
    
    public bool CanManageSyncs()
        => VM.Capabilities.HasCloudSync
           && !VM.IsLoading
           && VM.ErrorMessage is null
           && VM.CanManageSyncs;

    /// <summary>
    /// Scoped right to create public share links on the current share
    /// (ManagementPermission.ManageShareLinks). Gates the "share" context-menu entry.
    /// </summary>
    public bool CanManageShareLinks()
        => !VM.IsLoading
           && VM.ErrorMessage is null
           && VM.CanManageShareLinks;

    // ========== Virtual (Cloud Access) share ACLs ==========

    private bool _showVirtualAclPanel;
    private readonly Dictionary<Guid, CloudAccessPermission> _virtualGrants = new();
    private IReadOnlyList<PrincipalPickerItem> _virtualPrincipals = [];

    /// <summary>
    /// True when the browser sits at the root of a virtual (Cloud Access) share the actor
    /// may manage. Gates the "manage permissions" action for virtual shares, mirroring
    /// <see cref="CanManageAcls"/> for local shares (which are ACL'd per path instead).
    /// </summary>
    public bool CanManageVirtualShareAcls()
        => VM.CanManageVirtualShareAcls
           && VM.CurrentBrowserShare?.Kind == BrowserShareKind.Remote
           && string.IsNullOrEmpty(VM.CurrentPath)
           && !VM.IsLoading
           && VM.ErrorMessage is null;

    private async Task ToggleVirtualAclPanel()
    {
        _showVirtualAclPanel = !_showVirtualAclPanel;
        _showAclPanel = false;
        _showFolderAcl = false;
        if (_showVirtualAclPanel && VM.CurrentBrowserShare is { } share)
            await LoadVirtualGrantsAsync(share.Id);
    }

    private async Task LoadVirtualGrantsAsync(Guid shareId)
    {
        try
        {
            var grants = await CloudAcl.LoadShareGrantsAsync(shareId);
            _virtualGrants.Clear();
            foreach (var (principalId, level) in grants)
                _virtualGrants[principalId] = level;
            _virtualPrincipals = CloudAcl.AvailablePrincipals
                .Select(p => new PrincipalPickerItem(p.Id, p.Name, p is Group ? PrincipalKind.Group : PrincipalKind.User))
                .ToList();
        }
        catch (Exception exception)
        {
            _showVirtualAclPanel = false;
            Toast.Show(exception.Message, ToastType.Error);
        }
    }

    private async Task SaveVirtualGrantsAsync()
    {
        if (VM.CurrentBrowserShare is not { } share) return;
        try
        {
            await CloudAcl.UpdateShareGrantsAsync(share.Id, _virtualGrants);
        }
        catch (Exception exception)
        {
            Toast.Show(exception.Message, ToastType.Error);
        }
    }
}

