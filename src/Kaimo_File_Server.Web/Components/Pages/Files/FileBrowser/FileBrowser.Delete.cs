using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Web;

namespace Kaimo_File_Server.Web.Components.Pages.Files.FileBrowser;

/// <summary>Confirmed delete of the selected items, run as one cancellable batch.</summary>
public partial class FileBrowser
{
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
        // Pinned before the first await: the batch stays in this share even when the user
        // opens another one meanwhile.
        var operationTarget = VM.CaptureOperationTarget();
        var (batchShare, batchPath) = (ShareName, SubPath);
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

        using var undoGroup = VM.BeginUndoGroup();
        var progressThrottle = new ProgressThrottle();
        var done = 0;
        foreach (var target in targets)
        {
            if (job.CancellationToken.IsCancellationRequested)
                break;

            var result = await VM.DeleteAsync(target, operationTarget);
            if (!result.Success)
            {
                Toast.Update(
                    toastId,
                    result.Error ?? Resources.Web_Error_DeleteFailed,
                    type: ToastType.Error);
                await RefreshAfterBatchAsync(batchShare, batchPath);
                return;
            }

            deletedNames.Add(target.Name);
            done++;
            if (!progressThrottle.ShouldReport(isFinal: done == targets.Count))
                continue;
            var percent = targets.Count == 0 ? 100 : done * 100 / targets.Count;
            job.Update(string.Format(T("Web_Jobs_ItemCounter"), done, targets.Count), percent);
            Toast.Update(
                toastId,
                string.Format(T("Web_Delete_Progress"), done, targets.Count),
                progress: percent);
        }

        // Cancelled: never claim the whole selection is gone.
        if (deletedNames.Count < targets.Count)
            Toast.Update(
                toastId,
                string.Format(T("Web_Delete_BatchStopped"), deletedNames.Count, targets.Count),
                type: ToastType.Info);
        else
            Toast.Update(
                toastId,
                deletedNames.Count == 1
                    ? string.Format(Resources.Web_Delete_Success, deletedNames[0])
                    : string.Format(Resources.Web_Delete_BatchSuccess, deletedNames.Count),
                type: ToastType.Success);
        await RefreshAfterBatchAsync(batchShare, batchPath);
    }
}
