using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public partial class ShareListViewModel
{
    // -- Delete --

    public async Task<bool> DeleteShareAsync()
    {
        if (SelectedShare is null) return false;

        EditErrorMessage = null;

        if (!EnsureSelectedShareWritable())
            return false;

        // Server-side authorization: the razor hides the button when the actor
        // cannot manage this share, but the view-model method must guard too —
        // it enforces the specific DeleteShares right AND the department scope.
        if (!await CanManageSelectedShareAsync(ManagementPermission.DeleteShares))
        { EditErrorMessage = Resources.Web_Error_NoPermission; return false; }

        try
        {
            var cloudOperation = await TryBeginCloudSafeShareMutationAsync(
                SelectedShare.Id);
            if (!cloudOperation.Acquired)
            {
                EditErrorMessage = Resources.Web_Error_ShareInUse;
                return false;
            }
            await using var cloudOperationLease = cloudOperation.Lease;
            var name = SelectedShare.Name;
            // Delete share-scoped lifecycle data before dropping the definition;
            // version cleanup also reclaims blobs that are no longer referenced.
            if (_versionService != null)
                await _versionService.DeleteShareAsync(SelectedShare.Id);
            await _aclService.DeleteShareMetadataAsync(SelectedShare.Id);
            await _shareRepo.DeleteAsync(SelectedShare.Id);
            _lockManager.RemoveLock(name);

            _logger.LogInformation("Share '{ShareName}' deleted", name);

            DeselectShare();
            await LoadAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting the share");
            EditErrorMessage = Resources.Web_Error_DeleteFailed;
            return false;
        }
    }

    public async Task DisconnectCurrentShareFromCloud()
    {
        if(SelectedShare is null)
            return;

        EditErrorMessage = null;
        if (!EnsureSelectedShareWritable())
            return;

        var cloudOperation = await TryBeginCloudSafeShareMutationAsync(
            SelectedShare.Id);
        if (!cloudOperation.Acquired)
        {
            EditErrorMessage = Resources.Web_Error_ShareInUse;
            return;
        }
        await using var cloudOperationLease = cloudOperation.Lease;

        SelectedShare.CloudConnection?.Dispose();
        SelectedShare.CloudSettings = new CloudSettings(new Dictionary<string, SyncedFolder>());
        SelectedShare.CloudConnection = null;
        await _shareRepo.UpdateAsync(SelectedShare);
    }

    private async Task<(bool Acquired, ICloudSyncOperationLease? Lease)>
        TryBeginCloudSafeShareMutationAsync(Guid shareId)
    {
        if (_cloudSyncOperations is null)
            return (true, null);

        var lease = await _cloudSyncOperations.TryBeginShareMutationAsync(
            shareId);
        return (lease is not null, lease);
    }
}
