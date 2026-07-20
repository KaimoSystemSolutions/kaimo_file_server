
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

    private FilePreview _filePreviewComponent;
    private ContextMenu _contextMenuComponent;
    private VersionDialog _versionDialogComponent = default!;
    private PropertiesDialog _propertiesDialogComponent = default!;

    private ElementReference _fileDropZone;
    private bool _jsInitialized = false;
    
    // ========== Copy & Cut ==========
    private HashSet<FileMetadata> _clipboard = new();
    private bool _deleteOnPaste = false;
    private string? _clipboardToastID = null;
    private string? _clipboardStartPath = null;
    
    // ========== Drag & Drop ==========
    private HashSet<FileMetadata> _draggedItems = new();
    private string? _dragOverPath; // Path of the currently hovered drop target, ".." for the parent row

    // ========== Toolbar Computed ==========

    private bool CanOpenSelection => _selectedItems.Count == 1;
    private bool CanRenameSelection => _selectedItems.Count == 1;
    private bool CanDeleteSelection => _selectedItems.Count > 0;
    private bool CanAclSelection => CanManageAcls() && _selectedItems.Count == 1;

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
        if (_selectedItems.Count != 1) return;
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
        if(_selectedItems.Count < 1)
            return;

        if(_clipboardToastID != null)
            Toast.Remove(_clipboardToastID!);
        
        ClearClipboard();
        _clipboardStartPath = VM.CurrentPath;
        _deleteOnPaste = deleteOnPaste;
        
        foreach (var item in _selectedItems)
            _clipboard.Add(item);
        
        string verb = deleteOnPaste ? "Cutting" : "Copying";
        string multipleS = _clipboard.Count > 1 ? "s" : "";
        string msg = $"{verb}: {_selectedItems.Count} item{multipleS}";

        _clipboardToastID = Toast.Show(msg, ToastType.State, ClearClipboard);
    }

    private async Task PasteClipboard()
    {
        if(_clipboard.Count < 1)
            return;

        CancellationToken cancellationToken = new CancellationToken();
        
        Toast.Remove(_clipboardToastID);
        _clipboardToastID = Toast.Show("Pasting...", ToastType.Progress);

        // collect all items and sub items
        List<FileMetadata> itemsToCopy = new();
        
        foreach (FileMetadata file in _clipboard)
            await listItemsRecursivley(itemsToCopy, file);
        
        int filesPatedCount = 0;
        
        foreach (FileMetadata file in itemsToCopy)
        {
            string oldLocalPath = file.Path.Substring(_clipboardStartPath!.Length);
            string newPath = VM.CurrentPath + "/" + oldLocalPath;
            
            if (file.IsDirectory)
            {
                await VM.CreateFolderAtAsync(newPath);
                continue;
            }
            
            await VM.CopyAsync(file, newPath, cancellationToken);

            Toast.Update(_clipboardToastID, progress: (int)(++filesPatedCount * 100f / itemsToCopy.Count));
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

    internal async Task ClearClipboard()
    {
        _clipboard.Clear();
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

    private bool CanDropOnParent() => _draggedItems.Count > 0 && VM.HasParent;

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

        if (items.Count == 0) return;

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
}

