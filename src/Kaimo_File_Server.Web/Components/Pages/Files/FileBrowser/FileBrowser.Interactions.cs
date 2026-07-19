
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
    private enum ClipboardMode { None, Copy, Cut }
    private ClipboardMode _clipboardMode = ClipboardMode.None;
    private List<FileMetadata> _clipboardItems = new();
    
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

    // ========== Clipboard (Copy / Cut / Paste) ==========

private bool CanCopySelection => _selectedItems.Count > 0;
private bool CanCutSelection => _selectedItems.Count > 0;
internal bool CanPasteHere => _clipboardMode != ClipboardMode.None && _clipboardItems.Count > 0;

private bool IsCutItem(FileMetadata entry)
    => _clipboardMode == ClipboardMode.Cut && _clipboardItems.Contains(entry);

internal void CopySelected()
{
    if (_selectedItems.Count == 0) return;
    _clipboardItems = _selectedItems.ToList();
    _clipboardMode = ClipboardMode.Copy;

    Toast.Show(
        _clipboardItems.Count == 1
            ? string.Format(Resources.Web_Clipboard_Copied, _clipboardItems[0].Name)
            : string.Format(Resources.Web_Clipboard_CopiedBatch, _clipboardItems.Count),
        ToastType.Success);

    StateHasChanged();
}

internal void CutSelected()
{
    if (_selectedItems.Count == 0) return;
    _clipboardItems = _selectedItems.ToList();
    _clipboardMode = ClipboardMode.Cut;

    Toast.Show(
        _clipboardItems.Count == 1
            ? string.Format(Resources.Web_Clipboard_Cut, _clipboardItems[0].Name)
            : string.Format(Resources.Web_Clipboard_CutBatch, _clipboardItems.Count),
        ToastType.Success);

    StateHasChanged(); // so cut items render dimmed immediately
}

internal async Task PasteFromClipboard()
{
    if (!CanPasteHere) return;

    var items = _clipboardItems.ToList();
    var mode = _clipboardMode;
    var destPath = VM.CurrentPath;

    var toastId = Toast.Show(
        mode == ClipboardMode.Cut
            ? (items.Count == 1
                ? string.Format(Resources.Web_Move_Progress, items[0].Name)
                : string.Format(Resources.Web_Move_BatchProgress, items.Count))
            : (items.Count == 1
                ? string.Format(Resources.Web_Copy_Progress, items[0].Name)
                : string.Format(Resources.Web_Copy_BatchProgress, items.Count)),
        ToastType.Progress);

    var failed = new List<string>();
    foreach (var item in items)
    {
        var result = mode == ClipboardMode.Cut
            ? await VM.MoveAsync(item, destPath)
            : await VM.CopyAsync(item, destPath);

        if (!result.Success)
            failed.Add(item.Name);
    }

    if (mode == ClipboardMode.Cut)
    {
        // The items moved away — nothing left to paste again.
        _clipboardMode = ClipboardMode.None;
        _clipboardItems.Clear();
    }
    // Copy clipboard is intentionally kept, so the user can paste it elsewhere too.

    if (failed.Count == 0)
    {
        Toast.Update(toastId,
            mode == ClipboardMode.Cut
                ? (items.Count == 1
                    ? string.Format(Resources.Web_Move_Success, items[0].Name)
                    : string.Format(Resources.Web_Move_BatchSuccess, items.Count))
                : (items.Count == 1
                    ? string.Format(Resources.Web_Copy_Success, items[0].Name)
                    : string.Format(Resources.Web_Copy_BatchSuccess, items.Count)),
            type: ToastType.Success);
    }
    else
    {
        Toast.Update(toastId,
            string.Format(Resources.Web_Paste_PartialFailure, items.Count - failed.Count, items.Count),
            type: ToastType.Error);
    }

    _selectedItems.Clear();
    await VM.LoadShareAsync(ShareName, SubPath ?? "");
    StateHasChanged();
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

