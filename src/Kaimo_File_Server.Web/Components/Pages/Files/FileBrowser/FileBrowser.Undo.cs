using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Web.Services;
using Microsoft.JSInterop;

namespace Kaimo_File_Server.Web.Components.Pages.Files.FileBrowser;

public partial class FileBrowser
{
// ========== Undo ==========

    private bool _undoRunning;

    /// <summary>
    /// Reverts the last file operation in the open folder: toolbar button and page-level
    /// Ctrl+Z, provided no dialog or context menu currently owns the keyboard.
    /// </summary>
    [JSInvokable]
    public Task UndoLast() => InvokeAsync(async () =>
    {
        if (IsOverlayOpen || _undoRunning || !VM.CanUndo)
            return;

        _undoRunning = true;
        try
        {
            var result = await VM.UndoAsync();
            Toast.Show(
                result.Success ? Resources.Web_Undo_Done : result.Error ?? Resources.Web_Undo_Failed,
                result.Success ? ToastType.Success : ToastType.Error);
            _selectedItems.Clear();
            await VM.RefreshCurrentDirectoryAsync();
        }
        finally
        {
            _undoRunning = false;
            StateHasChanged();
        }
    });
}
