using Kaimo_File_Server.Web.Services;

namespace Kaimo_File_Server.Web.Components.Pages.Files.FileBrowser;

public partial class FileBrowser
{
    private bool _showCloudToLocal;
    private bool _cloudToLocalBusy;
    private Guid _cloudToLocalTargetId;
    private string _cloudToLocalPath = string.Empty;
    private string? _cloudToLocalError;
    private List<LocalTransferTarget> _cloudToLocalTargets = [];

    private async Task ShowCloudToLocalDialogAsync()
    {
        _cloudToLocalError = null;
        _cloudToLocalTargetId = Guid.Empty;
        _cloudToLocalTargets = await CloudTransfer.GetTargetsAsync();
        if (_cloudToLocalTargets.Count == 0)
        {
            Toast.Show(T("Web_CloudAccess_Error_NoLocalTarget"), ToastType.Error);
            return;
        }
        _showCloudToLocal = true;
    }

    private void CancelCloudToLocal()
    {
        if (_cloudToLocalBusy) return;
        _showCloudToLocal = false;
    }

    private async Task ConfirmCloudToLocalAsync()
    {
        if (_cloudToLocalTargetId == Guid.Empty || VM.CurrentBrowserShare is null) return;
        _cloudToLocalBusy = true;
        _cloudToLocalError = null;
        try
        {
            var result = await CloudTransfer.CopyAsync(
                VM.CurrentBrowserShare.Id,
                _selectedItems.ToList(),
                _cloudToLocalTargetId,
                _cloudToLocalPath);
            if (!result.Success)
            {
                _cloudToLocalError = result.Error;
                return;
            }
            _showCloudToLocal = false;
            Toast.Show(T("Web_CloudAccess_CopySuccess"), ToastType.Success);
        }
        finally
        {
            _cloudToLocalBusy = false;
        }
    }
}
