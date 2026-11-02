using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Services;

namespace Kaimo_File_Server.Web.Components.Pages.Files.FileBrowser;

/// <summary>Copy, cut and paste of the file browser (within a share and across shares).</summary>
public partial class FileBrowser
{
    // ========== Copy & Cut ==========
    private HashSet<FileMetadata> _clipboard = new();
    private bool _deleteOnPaste = false;
    private string? _clipboardToastID = null;
    private string? _clipboardStartPath = null;
    private BrowserShareInfo? _clipboardSourceShare;

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
            BrowserClipboard.Set(_clipboardSourceShare, _clipboard, deleteOnPaste, _clipboardStartPath);
        
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
            _clipboardStartPath = BrowserClipboard.StartPath;
            _clipboardToastID = BrowserClipboard.ToastId;
        }
        if (!VM.Capabilities.CanCopy || _clipboard.Count < 1 || _clipboardSourceShare is null || VM.CurrentBrowserShare is null)
            return;

        // A cut is pasted once at a time: a second paste would copy the items again and delete
        // the originals twice. The claim is released when this paste ends, whatever its outcome.
        var cutGeneration = _deleteOnPaste ? BrowserClipboard.Generation : (int?)null;
        if (cutGeneration is { } claimed && !BrowserClipboard.TryBeginCutPaste(claimed))
        {
            Toast.Show(T("Web_Transfer_CutPasteRunning"), ToastType.Info);
            return;
        }

        try
        {
            // Crossing a share boundary is handled by the transfer service. It owns
            // authorization for both backends and never cuts a virtual source.
            if (!_clipboardSourceShare.IsSameShareAs(VM.CurrentBrowserShare))
                await PasteAcrossSharesAsync();
            else
                await PasteWithinShareAsync();
        }
        finally
        {
            if (cutGeneration is { } released)
                BrowserClipboard.EndCutPaste(released);
        }
    }

    /// <summary>
    /// Paste across a share boundary via the transfer service. Wrapped so an
    /// unexpected throw never escapes to the circuit or strands the progress toast.
    /// </summary>
    private async Task PasteAcrossSharesAsync()
    {
        // The transfer service writes here without recording undo steps.
        VM.ClearUndo();
        // Pinned like PasteWithinShareAsync: a clipboard set meanwhile stays untouched.
        var deleteOnPaste = _deleteOnPaste;
        var clipboardGeneration = BrowserClipboard.Generation;
        using var job = StartTransferJob();
        RemoveClipboardToast();
        var toastId = Toast.Show(T("Web_Transfer_Progress"), ToastType.Progress,
            onDismiss: () => { job.Cancel(); return Task.CompletedTask; });
        try
        {
            var result = await CrossShareTransfer.TransferAsync(
                _clipboardSourceShare!, _clipboard.ToList(), VM.CurrentBrowserShare!,
                VM.CurrentPath, deleteOnPaste, job.CancellationToken,
                (name, progress) =>
                {
                    var detail = string.Format(T("Web_Transfer_ProgressItem"), name);
                    job.Update(detail, progress);
                    Toast.Update(toastId, detail, progress, ToastType.Progress);
                });
            if (result.Success)
            {
                Toast.Show(deleteOnPaste ? T("Web_Transfer_CutSuccess") : T("Web_Transfer_CopySuccess"), ToastType.Success);
                await VM.RefreshCurrentDirectoryAsync();
                await ClearClipboardIfUnchanged(clipboardGeneration);
            }
            else
            {
                Toast.Show(result.Error ?? T("Web_Transfer_Error_Failed"), ToastType.Error);
            }
        }
        catch (OperationCanceledException)
        {
            Toast.Show(T("Web_Transfer_Error_Cancelled"), ToastType.Info);
        }
        catch (Exception)
        {
            Toast.Show(T("Web_Transfer_Error_Failed"), ToastType.Error);
        }
        finally
        {
            Toast.Remove(toastId);
        }
    }

    /// <summary>
    /// Paste inside the same share: copy the collected tree item by item, then (for a
    /// cut) delete the originals. Each item is isolated so one failure never aborts the
    /// batch, the progress toast is always removed, and originals are only deleted when
    /// every copy succeeded — a partially failed cut must never lose data.
    /// </summary>
    private async Task PasteWithinShareAsync()
    {
        // The source directory is required to map each item into the target. It can be
        // absent after a circuit was recreated without a full clipboard restore.
        if (_clipboardStartPath is null)
        {
            Toast.Show(T("Web_Transfer_Error_Failed"), ToastType.Error);
            return;
        }

        // The clipboard, share and target folder are pinned for the whole batch: a cut or
        // copy made meanwhile, or opening another share or folder, never changes what it does.
        var clipboardItems = _clipboard.ToList();
        var clipboardStartPath = _clipboardStartPath;
        var deleteOnPaste = _deleteOnPaste;
        var clipboardGeneration = BrowserClipboard.Generation;
        var operationTarget = VM.CaptureOperationTarget();
        var targetFolder = VM.CurrentPath;
        var (batchShare, batchPath) = (ShareName, SubPath);
        using var localJob = StartTransferJob();
        var cancellationToken = localJob.CancellationToken;
        // Copies and (for a cut) the source deletes revert as one entry.
        using var undoGroup = VM.BeginUndoGroup();

        RemoveClipboardToast();
        // Own id: a clipboard set meanwhile shows its state toast under _clipboardToastID.
        var progressToastId = Toast.Show(T("Web_Transfer_Progress"), ToastType.Progress,
            onDismiss: () => { localJob.Cancel(); return Task.CompletedTask; });

        var progressThrottle = new ProgressThrottle();
        var itemsToCopy = new List<FileMetadata>();
        int copied = 0;
        var failed = new List<string>();
        List<string> originalsKept = [];
        bool cancelled = false;

        try
        {
            foreach (FileMetadata file in clipboardItems)
                await listItemsRecursivley(itemsToCopy, file, operationTarget);

            for (var i = 0; i < itemsToCopy.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var file = itemsToCopy[i];
                var newPath = ResolvePastePath(file, clipboardStartPath, targetFolder);
                if (await TryPasteItemAsync(file, newPath, operationTarget, cancellationToken))
                    copied++;
                else
                    failed.Add(file.Name);

                if (!progressThrottle.ShouldReport(isFinal: i == itemsToCopy.Count - 1))
                    continue;
                var progress = (int)(copied * 100f / Math.Max(1, itemsToCopy.Count));
                var detail = string.Format(T("Web_Transfer_ProgressItem"), file.Name);
                localJob.Update(detail, progress);
                Toast.Update(progressToastId, detail, progress, ToastType.Progress);
                StateHasChanged();
            }

            // Delete originals only for a cut where every copy succeeded.
            if (deleteOnPaste && failed.Count == 0)
            {
                originalsKept = await DeleteCutOriginalsAsync(
                    itemsToCopy,
                    item => VM.DeleteAsync(item, operationTarget),
                    cancellationToken.ThrowIfCancellationRequested);
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception)
        {
            Toast.Show(T("Web_Transfer_Error_Failed"), ToastType.Error);
        }
        finally
        {
            Toast.Remove(progressToastId);
            await RefreshAfterBatchAsync(batchShare, batchPath);
            StateHasChanged();
        }

        if (cancelled)
        {
            Toast.Show(T("Web_Transfer_Error_Cancelled"), ToastType.Info);
        }
        else if (originalsKept.Count > 0)
        {
            // Every copy exists but the cut is incomplete: keep the clipboard so it can be retried.
            Toast.Show(string.Format(T("Web_Transfer_CutOriginalsKept"), originalsKept.Count),
                ToastType.Error, tooltip: string.Join(Environment.NewLine, originalsKept));
        }
        else if (failed.Count == 0)
        {
            Toast.Show(deleteOnPaste ? T("Web_Transfer_CutSuccess") : T("Web_Transfer_CopySuccess"), ToastType.Success);
            await ClearClipboardIfUnchanged(clipboardGeneration);
        }
        else if (copied > 0)
        {
            Toast.Show(string.Format(T("Web_Transfer_PartialFailure"), copied, itemsToCopy.Count),
                ToastType.Error, tooltip: string.Join(Environment.NewLine, failed));
        }
        else
        {
            Toast.Show(T("Web_Transfer_Error_Failed"), ToastType.Error);
        }
    }

    /// <summary>
    /// Copies one item of a paste batch (a folder is created, its contents are separate items).
    /// False when it cannot be pasted: <paramref name="newPath"/> is null (not under the recorded
    /// source root), equals the item's own path (pasting into the source folder would overwrite
    /// it with itself), or the copy failed. Cancellation propagates.
    /// </summary>
    private async Task<bool> TryPasteItemAsync(
        FileMetadata file, string? newPath, FileOperationTarget? operationTarget, CancellationToken cancellationToken)
    {
        if (newPath is null || string.Equals(newPath, file.Path, StringComparison.Ordinal))
            return false;
        try
        {
            if (file.IsDirectory)
                await VM.CreateFolderAtAsync(newPath, operationTarget);
            else
                await VM.CopyAsync(file, newPath, cancellationToken, operationTarget);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes the originals of a cut whose copies all succeeded, innermost first so each folder
    /// is empty when removed. Returns the names that could not be deleted; one failure never stops
    /// the rest, and every copy exists, so nothing is lost. <paramref name="throwIfStopped"/> runs
    /// before each item (cancellation).
    /// </summary>
    internal static async Task<List<string>> DeleteCutOriginalsAsync(
        IReadOnlyList<FileMetadata> items, Func<FileMetadata, Task<OperationResult>> delete, Action throwIfStopped)
    {
        var failed = new List<string>();
        for (var i = items.Count - 1; i >= 0; i--)
        {
            throwIfStopped();
            if (!(await delete(items[i])).Success)
                failed.Add(items[i].Name);
        }
        return failed;
    }

    /// <summary>
    /// Maps a clipboard item's share-relative path into the current directory, preserving
    /// its position beneath the folder it was cut/copied from. Returns null when the item
    /// does not sit under that recorded source folder (a stale or malformed clipboard).
    /// </summary>
    private static string? ResolvePastePath(FileMetadata file, string clipboardStartPath, string targetFolder)
    {
        var root = clipboardStartPath.Trim('/');
        string relative;
        if (root.Length == 0)
            relative = file.Path.TrimStart('/');
        else if (file.Path.StartsWith(root + "/", StringComparison.Ordinal))
            relative = file.Path.Substring(root.Length + 1);
        else
            return null;

        var target = targetFolder.Trim('/');
        return target.Length == 0 ? relative : target + "/" + relative;
    }

    private async Task listItemsRecursivley(
        List<FileMetadata> itemList, FileMetadata item, FileOperationTarget? operationTarget)
    {
        itemList.Add(item);

        if (!item.IsDirectory)
            return;

        List<FileMetadata> folderItems = await VM.ListDirectoryAsync(item.Path, operationTarget);

        foreach (var innerItem in folderItems)
            await listItemsRecursivley(itemList, innerItem, operationTarget);
        
    }

    internal Task ClearClipboard()
    {
        _clipboard.Clear();
        _clipboardSourceShare = null;
        BrowserClipboard.Clear();
        return Task.CompletedTask;
    }

    // A finished paste clears only the clipboard it pasted, never one the user set meanwhile,
    // also not in another browser page of the circuit: the generation is circuit-wide.
    private Task ClearClipboardIfUnchanged(int pastedGeneration)
        => BrowserClipboard.Generation == pastedGeneration ? ClearClipboard() : Task.CompletedTask;

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
}
