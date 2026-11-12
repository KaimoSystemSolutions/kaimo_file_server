
using System.Collections;
using System.Diagnostics;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
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

    // Explicit refresh (context menu) also ends the undo history of this folder and
    // bypasses cached listings (virtual shares).
    public async Task Refresh()
    {
        VM.ClearUndo();
        VM.InvalidateListingCache();
        await OnParametersSetAsync();
    }

    protected override async Task OnParametersSetAsync()
    {
        _selectedItems.Clear();
        _sortColumn = null;
        _sortDirection = 0;
        _showCreateFolder = false;

        _contextMenuComponent?.CloseContextMenu();

        // Entering another directory moves keyboard focus to the item list, so Ctrl+V/C/X,
        // Delete and F2 work right away without clicking into the list first.
        var location = $"{ShareName}/{EffectiveLoadSubPath}";
        if (location != _lastLoadedLocation)
        {
            _lastLoadedLocation = location;
            _focusItemListAfterRender = true;
        }

        var (previousShare, previousPath) = (_loadedShareName, VM.CurrentPath ?? "");
        await VM.LoadShareAsync(ShareName, EffectiveLoadSubPath);
        _loadedShareName = ShareName;
        UpdateAclPath();
        await LoadAclCounts();
        VM.OnStateChanged -= OnVmStateChanged;
        VM.OnStateChanged += OnVmStateChanged;

        // A search hit selects its own entry; otherwise going up re-selects where we came from.
        if (!TryApplyPendingFileSelection())
            SelectChildWeCameFrom(previousShare, previousPath);

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

    // Share the listing was last loaded for, so a path comparison never spans two shares.
    private string? _loadedShareName;

    /// <summary>
    /// After navigating to an ancestor (".." row, Backspace, breadcrumb, browser Back), selects
    /// the child folder on the way to the previous location and scrolls it into view, so the
    /// user keeps their place. A no-op for any other navigation or a same-folder reload.
    /// </summary>
    private void SelectChildWeCameFrom(string? previousShare, string previousPath)
    {
        if (previousShare != ShareName)
            return;

        var name = ChildToward(VM.CurrentPath ?? "", previousPath);
        if (name is null)
            return;

        var dir = SortedDirectories.FirstOrDefault(d => d.Name == name);
        if (dir is null)
            return;

        _selectedItems.Add(dir);
        _anchorItem = _cursorItem = dir; // arrow keys and Shift continue from here
        _pendingScrollIndex = SortedEntries.IndexOf(dir);
    }

    /// <summary>
    /// First path segment below <paramref name="ancestor"/> on the way to
    /// <paramref name="descendant"/>, or <c>null</c> when the descendant is not strictly
    /// beneath the ancestor. Both are share-relative paths.
    /// </summary>
    internal static string? ChildToward(string ancestor, string descendant)
    {
        ancestor = ancestor.Trim('/');
        descendant = descendant.Trim('/');
        var prefix = ancestor.Length == 0 ? "" : ancestor + "/";
        if (descendant.Length <= prefix.Length || !descendant.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        return descendant[prefix.Length..].Split('/')[0];
    }

    private string? _lastLoadedLocation;
    private bool _focusItemListAfterRender;

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
        // Every folder load swaps the list for the loading view and back, so the list
        // is a new DOM element afterwards. Re-bind the JS helpers to each new element;
        // otherwise the marquee keeps listening on the detached one.
        if (_fileDropZone.Id is not null && _fileDropZone.Id != _jsInitializedForId)
        {
            _jsInitializedForId = _fileDropZone.Id;
            _dotNetRef ??= DotNetObjectReference.Create(this);
            if (VM.Capabilities.CanUpload)
                await JS.InvokeVoidAsync("initFileUpload", "#file-drop-zone", _dotNetRef);
            await JS.InvokeVoidAsync("fileMarquee.initialize", "#file-selection-area", _dotNetRef);
            if (VM.Capabilities.CanMove)
                await JS.InvokeVoidAsync("initInternalDragDrop");
        }

        // The list is only in the DOM once loading finished; until then the reference is stale.
        if (_focusItemListAfterRender && !VM.IsLoading && VM.ErrorMessage is null && _fileDropZone.Id is not null)
        {
            _focusItemListAfterRender = false;
            await TryFocusAsync(_fileDropZone, preventScroll: true);
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

    /// <summary>Toolbar "Manage permissions": the browsed folder's ACLs in the properties dialog.</summary>
    private Task OpenCurrentFolderPermissions()
    {
        var name = _aclPath == ""
            ? VM.CurrentBrowserShare?.Name ?? Resources.Web_Acl_ShareRoot
            : System.IO.Path.GetFileName(_aclPath);
        var folder = new FileMetadata { Name = name, Path = _aclPath, IsDirectory = true };
        return _propertiesDialogComponent.OpenPermissions(folder, securityOnly: true);
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
        // A mounted virtual-share entry can only be opened (see IsMountEntry).
        if (SelectionTouchesMount && e.Key is "Delete" or "F2" or "c" or "x")
            return;

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

    /// <summary>Opens the properties dialog for a multi-selection (combined values, no ACL tab).</summary>
    public async Task OpenPropertiesDialog(IReadOnlyList<FileMetadata> items)
        => await _propertiesDialogComponent.Open(items);

    /// <summary>Opens the properties dialog of a single item on its Permissions (Security) tab.</summary>
    public Task OpenPermissionsDialog(FileMetadata item)
        => _propertiesDialogComponent.OpenPermissions(item);

    /// <summary>Opens the access list of a virtual share mounted as a folder (mount entry).</summary>
    public void OpenMountPermissions(MountedFolder mount, FileMetadata entry)
        => _propertiesDialogComponent.OpenMountPermissions(mount, entry);

    /// <summary>Opens the "create hash" dialog for a single file.</summary>
    public async Task OpenHashDialog(FileMetadata item)
        => await _hashDialogComponent.Open(item);

    public async Task OpenShareLinkDialog(FileMetadata item)
        => await _shareLinkDialogComponent.Open(item);

    public async Task OpenUploadLinkDialog(FileMetadata folder)
        => await _shareLinkDialogComponent.Open(folder, ShareLinkKind.Upload);

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
    private static string SyncIcon(Core.Services.CloudSync.SyncMode mode) => mode switch
    {
        Core.Services.CloudSync.SyncMode.Pull => "sync-pull.svg",
        Core.Services.CloudSync.SyncMode.Push => "sync-push.svg",
        _ => "sync-twoway.svg"
    };

    /// <summary>Emblem icon: synced entries keep the direction glyph; unsynced ones warn.</summary>
    private static string SyncMarkerIcon(SyncFolderMarker marker) => marker.State switch
    {
        SyncItemState.PendingUpload => "upload.svg",   // will still be uploaded
        SyncItemState.PullBlocked => "warning.svg",    // pull-only: never uploaded
        _ => SyncIcon(marker.Mode)
    };

    /// <summary>Info tint for a synced (or currently syncing) entry, warning tint for anything still pending.</summary>
    private static string SyncMarkerColor(SyncItemState state)
        => IsSyncQuiet(state) ? "var(--info)" : "var(--warning)";

    private static string SyncIndicatorClass(SyncItemState state)
        => IsSyncQuiet(state) ? "" : "sync-indicator--warn";

    // A running sync decides the final state itself, so it never warns in the meantime.
    private static bool IsSyncQuiet(SyncItemState state)
        => state is SyncItemState.Synced or SyncItemState.Syncing;

    private string SyncMarkerTooltip(SyncFolderMarker marker) => marker.State switch
    {
        SyncItemState.Syncing => string.Format(Resources.Web_Sync_RunningTooltip, marker.SyncName),
        SyncItemState.PendingUpload => string.Format(Resources.Web_Sync_PendingUploadTooltip, marker.SyncName),
        SyncItemState.PullBlocked => string.Format(Resources.Web_Sync_PullBlockedTooltip, marker.SyncName),
        _ => string.Format(Resources.Web_Sync_FolderTooltip, marker.SyncName)
    };

    private string GetFileAclPath(FileMetadata file)
        => VM.ShareRelativeOf(file);

    // ========== Navigation ==========

    /// <summary>Double-click / open: navigate into a folder or preview a file.</summary>
    private async Task OnItemActivate(FileMetadata entry)
    {
        if (entry.IsDirectory)
            NavigateTo(entry);
        else
            await _filePreviewComponent.OpenFilePreview(entry);
    }

    /// <summary>Open the permissions dialog for a directory or a file (ACL emblem click).</summary>
    private Task OpenEntryAcl(FileMetadata entry) => OpenPermissionsDialog(entry);

    /// <summary>Jump to the external-storage sync overview, selecting the sync behind this emblem.</summary>
    private void OpenSyncOverview(SyncFolderMarker marker)
    {
        if (!CanManageSyncs() || marker.SyncId == Guid.Empty) return;
        Nav.NavigateTo($"/external-storage?sync={marker.SyncId}");
    }

    /// <summary>Jump to the share-link overview, selecting the link behind this emblem.</summary>
    private static readonly ShareLinkKind[] ShareLinkEmblemOrder = [ShareLinkKind.Download, ShareLinkKind.Upload];

    private void OpenShareLinkOverview(Guid linkId, ShareLinkKind kind)
    {
        if (!CanManageLink(kind)) return;
        Nav.NavigateTo($"/share-links?select={linkId}");
    }

    // Whether the user may open the overview entry of a link of the given kind.
    private bool CanManageLink(ShareLinkKind kind)
        => kind == ShareLinkKind.Upload ? CanManageUploadLinks() : CanManageShareLinks();

    internal void NavigateTo(FileMetadata dir)
        => NavigateToRoutePath(VM.RouteSubPathOf(VM.ShareRelativeOf(dir)));

    private void NavigateUp()
    {
        if (!VM.HasParent && Mount is { } mount)
        {
            // The host folder is a fresh browser; ask it to select the mount we came from.
            FileSelectionCoordinator.RequestSelection(
                mount.HostShareName, ShareRelativePath.GetParent(mount.MountPath), mount.Name);
            Nav.NavigateTo(ParentCrumbs![^1].Href);
            return;
        }
        NavigateToRoutePath(VM.RouteSubPathOf(VM.ParentPath ?? ""));
    }

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

        var (previousShare, previousPath) = (_loadedShareName, VM.CurrentPath ?? "");
        await VM.LoadShareAsync(ShareName, routeSubPath);
        _loadedShareName = ShareName;
        UpdateAclPath();
        SelectChildWeCameFrom(previousShare, previousPath);
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

    /// <summary>
    /// Scoped right to create public upload links on the current share
    /// (ManagementPermission.ManageUploadLinks). Gates the "upload link" context-menu entry.
    /// </summary>
    public bool CanManageUploadLinks()
        => !VM.IsLoading
           && VM.ErrorMessage is null
           && VM.CanManageUploadLinks;

    // ========== Virtual (Cloud Access) share ACLs ==========

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

    /// <summary>Toolbar "Manage permissions" at a virtual share's root: its access list in the properties dialog.</summary>
    private void OpenVirtualSharePermissions()
    {
        if (VM.CurrentBrowserShare is { } share)
            _propertiesDialogComponent.OpenVirtualSharePermissions(share.Id, share.Name);
    }
}

