using Kaimo_File_Server.Infrastructure.Services;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Web.Helpers;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public partial class ShareListViewModel
{
    // -- Rename (mit Lock) --

    public async Task<bool> RenameShareAsync()
    {
        if (SelectedShare is null) return false;

        EditErrorMessage = null;
        EditSuccessMessage = null;

        if (!EnsureSelectedShareWritable())
            return false;

        // Server-side authorization guard (see DeleteShareAsync).
        if (!await CanManageSelectedShareAsync(ManagementPermission.EditShareSettings))
        { EditErrorMessage = Resources.Web_Error_NoPermission; return false; }

        var newName = EditShareName.Trim();
        var oldName = SelectedShare.Name;

        if (newName == oldName)
        { EditErrorMessage = Resources.Web_Rename_Unchanged; return false; }

        if (!ValidateShareName(newName, out var error))
        { EditErrorMessage = error; return false; }

        var existingNew = await _shareRepo.GetByNameAsync(newName);
        if (existingNew is not null)
        { EditErrorMessage = Resources.Web_Error_ShareExists; return false; }

        var shareLock = _lockManager.GetLock(oldName);

        IsRenaming = true;
        try
        {
            // Lock holen – wartet bis alle laufenden Ops fertig sind
            if (!await shareLock.WaitAsync(TimeSpan.FromSeconds(30)))
            {
                EditErrorMessage = Resources.Web_Error_ShareInUse;
                return false;
            }

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
                // The persisted share path is the source of truth. Keep the
                // current pool and only replace the final directory component.
                var oldFullPath = Path.GetFullPath(SelectedShare.Path);
                var parentPath = Path.GetDirectoryName(oldFullPath)
                    ?? throw new InvalidOperationException("Share path has no parent directory.");
                var newFullPath = Path.Combine(parentPath, newName);

                if (!PathComparer.Equals(oldFullPath, newFullPath)
                    && (Directory.Exists(newFullPath) || File.Exists(newFullPath)))
                    throw new IOException($"Destination '{newFullPath}' already exists.");

                var directoryMoved = Directory.Exists(oldFullPath)
                    && !PathComparer.Equals(oldFullPath, newFullPath);
                if (directoryMoved)
                    Directory.Move(oldFullPath, newFullPath);

                // 2. ShareDefinition in DB updaten. If persistence fails, put
                // the directory back so the old persisted path remains valid.
                try
                {
                    await _shareRepo.UpdateLocationAsync(
                        SelectedShare.Id, newName, newFullPath);
                    SelectedShare.Name = newName;
                    SelectedShare.Path = newFullPath;
                }
                catch
                {
                    SelectedShare.Name = oldName;
                    SelectedShare.Path = oldFullPath;
                    if (directoryMoved && Directory.Exists(newFullPath))
                        Directory.Move(newFullPath, oldFullPath);
                    throw;
                }

                if (_searchService is not null
                    && !PathComparer.Equals(oldFullPath, newFullPath))
                {
                    try
                    {
                        await _searchService.onDirectoryRenamed(oldFullPath, newFullPath);
                    }
                    catch (Exception searchEx)
                    {
                        _logger.LogWarning(searchEx,
                            "Share '{ShareName}' renamed, but its search index could not be updated",
                            newName);
                    }
                }

                // 4. Lock-Key umbenennen
                _lockManager.RenameLock(oldName, newName);

                _logger.LogInformation("Share '{OldName}' renamed to '{NewName}'", oldName, newName);

                EditSuccessMessage = string.Format(Resources.Web_Share_RenamedTo, newName);
                EditShareName = newName;
                await LoadAsync();

                // Re-select mit neuen Daten
                var updated = Shares.FirstOrDefault(s => s.Id == SelectedShare.Id);
                if (updated is not null)
                {
                    SelectedShare = updated;
                    EditSharePoolPath = Path.GetDirectoryName(
                        Path.GetFullPath(updated.Path)) ?? EditSharePoolPath;
                }

                return true;
            }
            finally
            {
                shareLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error renaming share '{OldName}' -> '{NewName}'", oldName, newName);
            EditErrorMessage = Resources.Web_Error_RenameFailedCheckLogs;
            return false;
        }
        finally
        {
            IsRenaming = false;
        }
    }

    // -- Change storage pool --

    public async Task<bool> ChangeStoragePoolAsync()
    {
        if (SelectedShare is null) return false;

        EditErrorMessage = null;
        EditSuccessMessage = null;

        if (!EnsureSelectedShareWritable())
            return false;

        if (!await CanManageSelectedShareAsync(ManagementPermission.EditShareSettings))
        {
            EditErrorMessage = Resources.Web_Error_NoPermission;
            return false;
        }

        var targetPoolPath = GetConfiguredPoolPath(EditSharePoolPath);
        if (targetPoolPath is null)
        {
            EditErrorMessage = Resources.Web_Error_StoragePoolRequired;
            return false;
        }

        IsMovingPool = true;
        try
        {
            var outcome = await _poolMove.MoveAsync(SelectedShare, targetPoolPath);
            if (outcome.Result == SharePoolMoveResult.Unchanged)
            {
                EditSuccessMessage = Resources.Web_Share_StoragePoolUnchanged;
                return true;
            }
            if (outcome.Result != SharePoolMoveResult.Moved)
            {
                EditErrorMessage = PoolMoveMessages.ErrorFor(outcome);
                return false;
            }

            EditSuccessMessage = string.Format(
                Resources.Web_Share_StoragePoolChanged,
                StoragePools.First(p => PathComparer.Equals(
                    Path.GetFullPath(p.Path), Path.GetFullPath(targetPoolPath))).Name);

            await LoadAsync();
            var updated = Shares.FirstOrDefault(s => s.Id == SelectedShare.Id);
            if (updated is not null)
            {
                SelectedShare = updated;
                EditSharePoolPath = targetPoolPath;
            }

            return true;
        }
        finally
        {
            IsMovingPool = false;
        }
    }
}
