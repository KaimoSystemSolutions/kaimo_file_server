using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Infrastructure.Services;
using Kaimo_File_Server.Web.Helpers;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>Recycle-bin defaults and the home-folder recycle limits and pool move.</summary>
public partial class SettingsViewModel
{
    /// <summary>Global default size limit per recycle bin in bytes; 0 = unlimited.</summary>
    public long RecycleGlobalMaxSizeBytes { get; private set; }

    /// <summary>Limits of every home bin, or <c>null</c> while home folders are not set up.</summary>
    public RecycleLimits? HomesRecycleLimits { get; private set; }

    /// <summary>Pool chosen in the home-folder move form.</summary>
    public string HomesTargetPool { get; set; } = "";

    public bool IsMovingHomes { get; private set; }

    private async Task LoadRecycleAsync()
        => RecycleGlobalMaxSizeBytes = await _config.GetAsync(RecycleBinJanitor.GlobalMaxSizeConfigKey, 0L);

    /// <summary>Saves the global default; <c>null</c> = unlimited.</summary>
    public async Task<bool> SaveRecycleGlobalMaxSizeAsync(long? maxSizeBytes)
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (!CanManageSettings)
        {
            ErrorMessage = Resources.Web_Settings_NoPermissionChange;
            return false;
        }
        if (maxSizeBytes < ShareDefinition.MinRecycleMaxSizeBytes)
        {
            ErrorMessage = InvalidRecycleLimitsText;
            return false;
        }

        try
        {
            await _config.SetAsync(RecycleBinJanitor.GlobalMaxSizeConfigKey, maxSizeBytes ?? 0L);
            RecycleGlobalMaxSizeBytes = maxSizeBytes ?? 0L;
            _logger.LogInformation("Global recycle-bin size limit set to {Bytes} bytes (0 = unlimited)", RecycleGlobalMaxSizeBytes);
            SuccessMessage = RecycleSavedText;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving the global recycle-bin size limit failed");
            ErrorMessage = Resources.Web_Error_ChangeStatusFailed;
            return false;
        }
    }

    public async Task<bool> SaveHomesRecycleLimitsAsync(RecycleLimits limits)
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (!CanManageSettings || _homes is null)
        {
            ErrorMessage = Resources.Web_Settings_NoPermissionChange;
            return false;
        }
        if (!limits.IsValid)
        {
            ErrorMessage = InvalidRecycleLimitsText;
            return false;
        }

        try
        {
            if (!await _homes.SetRecycleLimitsAsync(limits))
            {
                ErrorMessage = Resources.Web_Error_ChangeStatusFailed;
                return false;
            }
            SuccessMessage = RecycleSavedText;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving the home-folder recycle-bin limits failed");
            ErrorMessage = Resources.Web_Error_ChangeStatusFailed;
            return false;
        }
        finally
        {
            await LoadHomesAsync();
        }
    }

    /// <summary>Moves the home-folder share, with every home, into <see cref="HomesTargetPool"/>.</summary>
    public async Task<bool> MoveHomesPoolAsync()
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (!CanManageSettings || _homes is null)
        {
            ErrorMessage = Resources.Web_Settings_NoPermissionChange;
            return false;
        }

        // Only a pool the server actually discovered may be chosen.
        var pool = StorageUsages.FirstOrDefault(u =>
            string.Equals(u.StoragePath, HomesTargetPool, StringComparison.Ordinal))?.StoragePath;
        if (pool is null)
        {
            ErrorMessage = Resources.Web_Error_StoragePoolRequired;
            return false;
        }

        IsMovingHomes = true;
        try
        {
            var outcome = await _homes.MovePoolAsync(pool);
            switch (outcome?.Result)
            {
                case SharePoolMoveResult.Moved:
                    SuccessMessage = string.Format(
                        Resources.ResourceManager.GetString("Web_Settings_Homes_Moved") ?? "Home folders moved to pool {0}.",
                        ResolvePoolName(pool));
                    return true;
                case SharePoolMoveResult.Unchanged:
                    SuccessMessage = Resources.Web_Share_StoragePoolUnchanged;
                    return true;
                case null:
                    ErrorMessage = Resources.ResourceManager.GetString("Web_Settings_Homes_ConfigureFailed")
                        ?? "Home folders could not be set up.";
                    return false;
                default:
                    ErrorMessage = PoolMoveMessages.ErrorFor(outcome);
                    return false;
            }
        }
        finally
        {
            IsMovingHomes = false;
            await LoadHomesAsync();
        }
    }

    private static string RecycleSavedText
        => Resources.ResourceManager.GetString("Web_Recycle_Saved") ?? "Recycle bin limits saved.";

    private static string InvalidRecycleLimitsText
        => Resources.ResourceManager.GetString("Web_Recycle_Error_Invalid")
           ?? "Invalid limits: the maximum size must be at least 1 MB and the retention 1 to 3650 days.";
}
