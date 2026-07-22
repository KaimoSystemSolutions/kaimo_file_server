
using System.Collections;
using System.Diagnostics;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Web.Components.Pages.Files.FileBrowser.components;
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

        _contextMenuComponent?.CloseContextMenu();

        await VM.LoadShareAsync(ShareName, SubPath ?? "");
        UpdateAclPath();
        await LoadAclCounts();
        VM.OnStateChanged -= OnVmStateChanged;
        VM.OnStateChanged += OnVmStateChanged;

        TryApplyPendingFileSelection();
    }

    private void OnFileSelectionRequested()
    {
        if (TryApplyPendingFileSelection())
            _ = InvokeAsync(StateHasChanged);
    }

    private bool TryApplyPendingFileSelection()
    {
        if (!FileSelectionCoordinator.TryConsume(ShareName, SubPath ?? "", out var itemName))
            return false;

        _selectedItems.Clear();
        var item = FindShareItem(itemName);
        if (item is not null)
            _selectedItems.Add(item);

        return true;
    }

    private void OnVmStateChanged() => InvokeAsync(StateHasChanged);

    public void Dispose()
    {
        VM.OnStateChanged -= OnVmStateChanged;
        FileSelectionCoordinator.SelectionRequested -= OnFileSelectionRequested;
        _dotNetRef?.Dispose();
        UploadCoordinator.OnFilesSelected -= OnFileUploaded;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_jsInitialized && _fileDropZone.Id is not null)
        {
            _jsInitialized = true;
            _dotNetRef = DotNetObjectReference.Create(this);
            await JS.InvokeVoidAsync("initFileUpload", "#file-drop-zone");
            await JS.InvokeVoidAsync("initInternalDragDrop");
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
        _createFolderError = null;
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
        var result = await VM.CreateFolderAsync(_newFolderName.Trim());
        if (result.Success)
        {
            _showCreateFolder = false;
            await VM.LoadShareAsync(ShareName, SubPath ?? "");
        }
        else
            _createFolderError = result.Error;
    }

    // ========== Key Input detection ==========
    
    private async Task OnKeyDownItemList(KeyboardEventArgs e)
    {
        if (e.Key == "Delete" && _selectedItems.Count > 0)
            await DeleteSelected();

        if (e.Key == "F2" && _selectedItems.Count == 1)
            await RenameSelected();
        
        // copy/cut/paste
        if (e.CtrlKey)
        {
            if (e.Key == "c")
                await PutIntoClipboard(false);
        
            if (e.Key == "x")
                await PutIntoClipboard(true);

            if (e.Key == "v")
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

        foreach (var target in targets)
        {
            var result = await VM.DeleteAsync(target);
            if (!result.Success)
            {
                _deleteError = result.Error;
                return;
            }

            deletedNames.Add(target.Name);
        }

        Toast.Show(
            deletedNames.Count == 1
                ? string.Format(Resources.Web_Delete_Success, deletedNames[0])
                : string.Format(Resources.Web_Delete_BatchSuccess, deletedNames.Count),
            ToastType.Success);

        _showDeleteConfirm = false;
        _deleteTargets.Clear();
        _selectedItems.Clear();
        await VM.LoadShareAsync(ShareName, SubPath ?? "");
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
        var result = await VM.RenameAsync(_renameTarget, _renameNewName.Trim());

        if (!result.Success)
            _renameError = result.Error;

        _selectedItems.Clear();
        _showRenameDialog = false;
        _renameTarget = null;

        await VM.LoadShareAsync(ShareName, SubPath ?? "");
        await LoadAclCounts();

        // put renamed item into the new selection
        var newItem = FindShareItem(_renameNewName);
        if (newItem is not null)
            _selectedItems.Add(newItem);
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
    {
        var relativePath = dir.Path;
        if (VM.CurrentShare is not null && relativePath.StartsWith(VM.CurrentShare.Path))
            relativePath = relativePath[VM.CurrentShare.Path.Length..].TrimStart('/');
        return relativePath;
    }

    private string GetFileAclPath(FileMetadata file)
    {
        var relativePath = file.Path;
        if (VM.CurrentShare is not null && relativePath.StartsWith(VM.CurrentShare.Path))
            relativePath = relativePath[VM.CurrentShare.Path.Length..].TrimStart('/');
        return relativePath;
    }

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
    {
        var relativePath = dir.Path;
        if (VM.CurrentShare is not null && relativePath.StartsWith(VM.CurrentShare.Path))
            relativePath = relativePath[VM.CurrentShare.Path.Length..].TrimStart('/');
        Nav.NavigateTo($"/files/{ShareName}/{relativePath}");
    }

    private void NavigateUp()
    {
        if (string.IsNullOrEmpty(VM.ParentPath))
            Nav.NavigateTo($"/files/{ShareName}");
        else
            Nav.NavigateTo($"/files/{ShareName}/{VM.ParentPath}");
    }

    /// <summary>
    /// Scoped ACL-management right for the current share, resolved server-side by
    /// the view model (ManagementPermission.ManageShareAcls on this share). Gates the
    /// ACL panel, badges and permission actions — and the context-menu entry.
    /// </summary>
    public bool CanManageAcls()
        => VM.CanManageAcls;
}

