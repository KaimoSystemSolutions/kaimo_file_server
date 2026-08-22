
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
// ========== State ==========

    private Dictionary<string, int> _aclCounts = new();
    private bool _showAclPanel;
    private string _aclPath = "";
    private bool _showFolderAcl;
    private string _aclFolderPath = "";
    private bool _showCreateFolder;
    private string _newFolderName = "";
    private string? _createFolderError;
    private bool _showDeleteConfirm;
    private List<FileMetadata> _deleteTargets = new();
    private string? _deleteError;
    private HashSet<FileMetadata> _selectedItems = new();
    private bool _showRenameDialog;
    private FileMetadata? _renameTarget;
    private string _renameNewName = "";
    private string? _renameError;

    private ElementReference _deleteModalRef;

    private FilePreview _filePreviewComponent = default!;
    private ContextMenu _contextMenuComponent = default!;
    private VersionDialog _versionDialogComponent = default!;
    private PropertiesDialog _propertiesDialogComponent = default!;

    private ElementReference _fileDropZone;
    private bool _jsInitialized = false;
    
    private SyncDialog? _syncDialogComponent;
    
    // ========== Copy & Cut ==========
    private HashSet<FileMetadata> _clipboard = new();
    private bool _deleteOnPaste = false;
    private string? _clipboardToastID = null;
    private string? _clipboardStartPath = null;
    private BrowserShareInfo? _clipboardSourceShare;
    
    // ========== Drag & Drop ==========
    private HashSet<FileMetadata> _draggedItems = new();
    private string? _dragOverPath; // Path of the currently hovered drop target, ".." for the parent row

    // ========== Toolbar Computed ==========
    
    private bool CanOpenSelection => VM.Capabilities.CanOpen && _selectedItems.Count == 1;
    private bool CanRenameSelection => VM.Capabilities.CanRename && _selectedItems.Count == 1;
    private bool CanDeleteSelection => VM.Capabilities.CanDelete && _selectedItems.Count > 0;
    private bool CanAclSelection => CanManageAcls() && _selectedItems.Count == 1;
    private bool CanPasteClipboard => _clipboard.Count > 0 || BrowserClipboard.Source is not null;
    
    // ========== Syncing ==========
    
    [SupplyParameterFromQuery(Name = "just_synced")]
    public string? JustSynced { get; set; }
    
    
    // ========== Selection ==========

    public void AddItemToSelection(FileMetadata item)
    {
        _selectedItems.Add(item);
        StateHasChanged();
    } 

    public void RemoveItemFromSelection(FileMetadata item)
    {
        _selectedItems.Remove(item);
        StateHasChanged();
    }

    public void ClearSelection()
    {
        _selectedItems.Clear();
        StateHasChanged();
    }
  
    public bool SelectedItemsContain(FileMetadata fileMetadata)
        => _selectedItems.Contains(fileMetadata);

    public int GetAmountSelectedItems()
        => _selectedItems.Count;

    public FileMetadata getFirstSelectedItem()
        => _selectedItems.First();

    public HashSet<FileMetadata> getSelectedItems()
        => _selectedItems;

    private void OnItemClick(MouseEventArgs e, FileMetadata item)
    {
        if (e.CtrlKey || e.MetaKey)
        {
            if (!_selectedItems.Remove(item))
                _selectedItems.Add(item);
        }
        else if (e.ShiftKey && _selectedItems.Count > 0)
        {
            var allItems = VM.Directories.Cast<FileMetadata>().Concat(VM.Files).ToList();
            var lastSelected = _selectedItems.Last();
            var idxStart = allItems.IndexOf(lastSelected);
            var idxEnd = allItems.IndexOf(item);
            if (idxStart >= 0 && idxEnd >= 0)
            {
                var from = Math.Min(idxStart, idxEnd);
                var to = Math.Max(idxStart, idxEnd);
                for (int i = from; i <= to; i++)
                    _selectedItems.Add(allItems[i]);
            }
        }
        else
        {
            if (_selectedItems.Count == 1 && _selectedItems.First() == item)
                _selectedItems.Clear();
            else
            {
                _selectedItems.Clear();
                _selectedItems.Add(item);
            }
        }
    }

    private void OnListBackgroundClick(MouseEventArgs e) => _selectedItems.Clear();

    /// <summary>
    /// Applies a completed Explorer-style marquee selection. JavaScript performs
    /// the pointer tracking locally and calls this once per gesture, keeping a
    /// large directory listing responsive while the pointer is moving.
    /// </summary>
    [JSInvokable]
    public Task CompleteMarqueeSelection(string[] paths, bool addToSelection)
    {
        var selectedPaths = paths.ToHashSet(StringComparer.Ordinal);
        var availableItems = VM.Directories.Cast<FileMetadata>().Concat(VM.Files);

        if (!addToSelection)
            _selectedItems.Clear();

        foreach (var item in availableItems)
        {
            if (selectedPaths.Contains(item.Path))
                _selectedItems.Add(item);
        }

        return InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Clears the file selection for a page-level Escape press, provided no
    /// dialog or context menu currently owns the key.
    /// </summary>
    [JSInvokable]
    public Task ClearSelectionOnEscape()
    {
        if (_showCreateFolder || _showDeleteConfirm || _showRenameDialog || _showCloudToLocal ||
            _filePreviewComponent?.IsOpen == true || _versionDialogComponent?.IsOpen == true ||
            _propertiesDialogComponent?.IsOpen == true || _contextMenuComponent?.IsOpen == true)
        {
            return Task.CompletedTask;
        }

        if (_selectedItems.Count == 0)
            return Task.CompletedTask;

        _selectedItems.Clear();
        return InvokeAsync(StateHasChanged);
    }

    // ========== Toolbar Actions ==========

    private async Task OpenSelected()
    {
        if (_selectedItems.Count != 1) return;
        var item = _selectedItems.First();

        if (item.IsDirectory)

            NavigateTo(item);
        else
            await _filePreviewComponent.OpenFilePreview(item);
    }

    internal async Task RenameSelected()
    {
        if (!VM.Capabilities.CanRename || _selectedItems.Count != 1) return;
        var item = _selectedItems.First();
        _renameTarget = item;
        _renameNewName = item.Name;
        _renameError = null;
        _showRenameDialog = true;

        // Wait one frame so the input is actually in the DOM
        await Task.Delay(50);
        await JS.InvokeVoidAsync("selectFileName", "#rename-input");
    }

    internal async Task PutIntoClipboard(bool deleteOnPaste)
    {
        if (!VM.Capabilities.CanCopy || (deleteOnPaste && !VM.Capabilities.CanCut) || _selectedItems.Count < 1)
            return;

        RemoveClipboardToast();
        
        await ClearClipboard();
        _clipboardStartPath = VM.CurrentPath;
        _clipboardSourceShare = VM.CurrentBrowserShare;
        _deleteOnPaste = deleteOnPaste;
        
        foreach (var item in _selectedItems)
            _clipboard.Add(item);
        if (_clipboardSourceShare is not null)
            BrowserClipboard.Set(_clipboardSourceShare, _clipboard, deleteOnPaste);
        
        var msg = _clipboard.Count == 1
            ? string.Format(T(deleteOnPaste ? "Web_Transfer_CuttingOne" : "Web_Transfer_CopyingOne"), _clipboard.First().Name)
            : string.Format(T(deleteOnPaste ? "Web_Transfer_CuttingMany" : "Web_Transfer_CopyingMany"), _clipboard.Count);
        var tooltip = _clipboard.Count > 1
            ? string.Join(Environment.NewLine, _clipboard.Select(item => item.Name).OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            : null;

        _clipboardToastID = Toast.Show(msg, ToastType.State, ClearClipboard, tooltip);
        BrowserClipboard.SetToastId(_clipboardToastID);
    }

    private async Task PasteClipboard()
    {
        if (_clipboard.Count == 0 && BrowserClipboard.Source is not null)
        {
            _clipboard = BrowserClipboard.Items.ToHashSet();
            _clipboardSourceShare = BrowserClipboard.Source;
            _deleteOnPaste = BrowserClipboard.DeleteOnPaste;
            _clipboardToastID = BrowserClipboard.ToastId;
        }
        if (!VM.Capabilities.CanCopy || _clipboard.Count < 1 || _clipboardSourceShare is null || VM.CurrentBrowserShare is null)
            return;

        // Crossing a share boundary is handled by the transfer service. It owns
        // authorization for both backends and never cuts a virtual source.
        if (_clipboardSourceShare.Id != VM.CurrentBrowserShare.Id || _clipboardSourceShare.Kind != VM.CurrentBrowserShare.Kind)
        {
            using var job = StartTransferJob();
            RemoveClipboardToast();
            var toastId = Toast.Show(T("Web_Transfer_Progress"), ToastType.Progress,
                onDismiss: () => { job.Cancel(); return Task.CompletedTask; });
            var result = await CrossShareTransfer.TransferAsync(
                _clipboardSourceShare, _clipboard.ToList(), VM.CurrentBrowserShare,
                VM.CurrentPath, _deleteOnPaste, job.CancellationToken,
                (name, progress) =>
                {
                    var detail = string.Format(T("Web_Transfer_ProgressItem"), name);
                    job.Update(detail, progress);
                    Toast.Update(toastId, detail, progress, ToastType.Progress);
                });
            Toast.Remove(toastId);
            if (result.Success)
            {
                Toast.Show(_deleteOnPaste ? T("Web_Transfer_CutSuccess") : T("Web_Transfer_CopySuccess"), ToastType.Success);
                await VM.RefreshCurrentDirectoryAsync();
                await ClearClipboard();
            }
            else
            {
                Toast.Show(result.Error ?? T("Web_Transfer_Error_Failed"), ToastType.Error);
            }
            return;
        }

        using var localJob = StartTransferJob();
        var cancellationToken = localJob.CancellationToken;
        
        RemoveClipboardToast();
        _clipboardToastID = Toast.Show(T("Web_Transfer_Progress"), ToastType.Progress,
            onDismiss: () => { localJob.Cancel(); return Task.CompletedTask; });

        // collect all items and sub items
        List<FileMetadata> itemsToCopy = new();
        
        foreach (FileMetadata file in _clipboard)
            await listItemsRecursivley(itemsToCopy, file);
        
        int filesPatedCount = 0;
        
        foreach (FileMetadata file in itemsToCopy)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string oldLocalPath = file.Path.Substring(_clipboardStartPath!.Length);
            string newPath = VM.CurrentPath + "/" + oldLocalPath;
            
            if (file.IsDirectory)
            {
                await VM.CreateFolderAtAsync(newPath);
                continue;
            }
            
            await VM.CopyAsync(file, newPath, cancellationToken);

            var progress = (int)(++filesPatedCount * 100f / itemsToCopy.Count);
            var detail = string.Format(T("Web_Transfer_ProgressItem"), file.Name);
            localJob.Update(detail, progress);
            Toast.Update(_clipboardToastID, detail, progress, ToastType.Progress);
            StateHasChanged();
            await VM.LoadShareAsync(ShareName, SubPath ?? "");
        }

        // has to reverse the order, so the most inner objects are deleted first.
        // this way, we delete folders once they are already empty
        itemsToCopy.Reverse();
        
        if(_deleteOnPaste)
            foreach (FileMetadata file in itemsToCopy)
                await VM.DeleteAsync(file);
        
        Toast.Remove(_clipboardToastID);

        string verb = _deleteOnPaste ? "cut" : "copied";
        string msg = $"Successfully {verb} {_clipboard.Count} files";
        Toast.Show(msg, ToastType.Success);
    }

    private async Task listItemsRecursivley(List<FileMetadata> itemList, FileMetadata item)
    {
        itemList.Add(item);

        if (!item.IsDirectory)
            return;
        
        List<FileMetadata> folderItems = await VM.ListDirectoryAsync(item.Path);

        foreach (var innerItem in folderItems)
            await listItemsRecursivley(itemList, innerItem);
        
    }

    internal Task ClearClipboard()
    {
        _clipboard.Clear();
        _clipboardSourceShare = null;
        BrowserClipboard.Clear();
        return Task.CompletedTask;
    }

    /// <summary>Removes the retained state toast even when its source page was already disposed.</summary>
    private void RemoveClipboardToast()
    {
        var toastId = _clipboardToastID ?? BrowserClipboard.ToastId;
        if (toastId is not null)
            Toast.Remove(toastId);
        _clipboardToastID = null;
        BrowserClipboard.ClearToastId();
    }

    /// <summary>Registers a navigation-independent, user-cancellable transfer in the global job menu.</summary>
    private JobHandle StartTransferJob()
    {
        var itemLabel = _clipboard.Count == 1 ? _clipboard.First().Name : string.Format(T("Web_Transfer_Items"), _clipboard.Count);
        var action = T(_deleteOnPaste ? "Web_Transfer_JobCut" : "Web_Transfer_JobCopy");
        return Jobs.Start(string.Format(T("Web_Jobs_FileTransfer_Title"), action, itemLabel),
            T("Web_Transfer_Progress"), "file-transfer");
    }

    internal async Task DeleteSelected()
    {
        if (_selectedItems.Count == 0) return;
        _deleteTargets = _selectedItems.ToList();
        _deleteError = null;
        _showDeleteConfirm = true;

        StateHasChanged();
        await Task.Yield(); // let Blazor render the modal first
        await _deleteModalRef.FocusAsync();
    }

    private void AclSelected()
    {
        if (_selectedItems.Count != 1) return;
        var item = _selectedItems.First();
        if (item.IsDirectory) OpenFolderAcl(item);
        else OpenFileAcl(item);
    }

// ---- Drag start/end ----

    private void OnDragStart(FileMetadata entry)
    {
        if (!VM.Capabilities.CanMove)
            return;

        // If the dragged item is part of the current selection, the entire
        // selection is dragged, otherwise only the single item.
        if (!_selectedItems.Contains(entry))
        {
            _selectedItems.Clear();
            _selectedItems.Add(entry);
        }

        _draggedItems = new HashSet<FileMetadata>(_selectedItems);
    }

    private void OnDragEnd()
    {
        _draggedItems.Clear();
        _dragOverPath = null;
    }

// ---- Drop target validation ----

    private bool CanDropOn(FileMetadata target)
    {
        if (!VM.Capabilities.CanMove) return false;
        if (_draggedItems.Count == 0) return false;
        if (!target.IsDirectory) return false;
        if (_draggedItems.Contains(target)) return false;

        // Prevents a folder from being moved into itself / its own subfolders
        foreach (var item in _draggedItems)
        {
            if (item.IsDirectory &&
                (target.Path == item.Path || target.Path.StartsWith(item.Path.TrimEnd('/') + "/")))
            {
                return false;
            }
        }

        return true;
    }

    private bool CanDropOnParent()
        => VM.Capabilities.CanMove && _draggedItems.Count > 0 && VM.HasParent;

// ---- Hover-Feedback ----

    private void OnDragEnterEntry(FileMetadata target)
    {
        if (CanDropOn(target))
            _dragOverPath = target.Path;
    }

    private void OnDragEnterParent()
    {
        if (CanDropOnParent())
            _dragOverPath = "..";
    }

    private void OnDragLeave() => _dragOverPath = null;

// ---- Drop-Handling ----

    private async Task OnDropOnEntry(FileMetadata target)
    {
        if (!CanDropOn(target))
        {
            OnDragEnd();
            return;
        }

        await MoveDraggedItemsTo(GetDirAclPath(target));
    }

    private async Task OnDropOnParent()
    {
        if (!CanDropOnParent())
        {
            OnDragEnd();
            return;
        }

        await MoveDraggedItemsTo(VM.ParentPath ?? "");
    }

    private async Task MoveDraggedItemsTo(string destRelativePath)
    {
        var items = _draggedItems.ToList();
        _draggedItems.Clear();
        _dragOverPath = null;

        if (!VM.Capabilities.CanMove || items.Count == 0) return;

        var toastId = Toast.Show(
            items.Count == 1
                ? string.Format(Resources.Web_Move_Progress, items[0].Name)
                : string.Format(Resources.Web_Move_BatchProgress, items.Count),
            ToastType.Progress);

        var failed = new List<string>();
        foreach (var item in items)
        {

            var result = await VM.MoveAsync(item, destRelativePath);
            if (!result.Success)
                failed.Add(item.Name);
        }

        if (failed.Count == 0)
        {
            Toast.Update(toastId,
                items.Count == 1
                    ? string.Format(Resources.Web_Move_Success, items[0].Name)
                    : string.Format(Resources.Web_Move_BatchSuccess, items.Count),
                type: ToastType.Success);
        }
        else
        {
            Toast.Update(toastId,
                string.Format(Resources.Web_Move_BatchPartialFailure, items.Count - failed.Count, items.Count),
                type: ToastType.Error);
        }

        _selectedItems.Clear();
        await VM.LoadShareAsync(ShareName, SubPath ?? "");
        StateHasChanged();
    }
    
    // -- Sync -- 
    
    public Task OpenSyncDialog(FileMetadata folder)
    {
        if (_syncDialogComponent is null || VM.CurrentShare is null)
            return Task.CompletedTask;

        return _syncDialogComponent.Open(VM.CurrentShare.Id, folder.Path);
    }
    
}

