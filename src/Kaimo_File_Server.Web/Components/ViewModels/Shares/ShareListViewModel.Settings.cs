using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Security;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public partial class ShareListViewModel
{
    // -- Toggle Share Enabled and Recycle Enabled and Share Hidden --

    public async Task<bool> ToggleShareEnabledAsync()
    {
        if (SelectedShare is null) return false;

        if (!EnsureSelectedShareWritable())
            return false;

        // Enabling/disabling a share governs whether anyone can access it → ManageShareAccess.
        if (!await CanManageSelectedShareAsync(ManagementPermission.ManageShareAccess))
        { EditSuccessMessage = null; EditErrorMessage = Resources.Web_Error_NoPermission; return false; }

        try
        {
            SelectedShare.IsEnabled = !SelectedShare.IsEnabled;
            await _shareRepo.UpdateAsync(SelectedShare);

            _logger.LogInformation("Share '{ShareName}' {Status}",
                SelectedShare.Name, SelectedShare.IsEnabled ? "enabled" : "disabled");
            // The in-memory share (shared with the list) is already updated and saved;
            // no full reload needed. Setting the message only here avoids the banner
            // blinking off/on, which shifts the whole panel (visible flicker).
            EditErrorMessage = null;
            EditSuccessMessage = SelectedShare.IsEnabled
                ? Resources.Web_Share_Enabled
                : Resources.Web_Share_Disabled;

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error changing the share status");
            EditSuccessMessage = null;
            EditErrorMessage = Resources.Web_Error_ChangeStatusFailed;
            return false;
        }
    }

    public async Task<bool> ToggleRecycleEnabledAsync()
    {
        if (SelectedShare is null) return false;

        if (!EnsureSelectedShareWritable())
            return false;

        // The recycle bin is a share feature setting → EditShareSettings.
        if (!await CanManageSelectedShareAsync(ManagementPermission.EditShareSettings))
        { EditSuccessMessage = null; EditErrorMessage = Resources.Web_Error_NoPermission; return false; }

        try
        {
            SelectedShare.IsRecycleEnabled = !SelectedShare.IsRecycleEnabled;
            await _shareRepo.UpdateAsync(SelectedShare);

            _logger.LogInformation("Recycle bin for share '{ShareName}' {Status}",
                SelectedShare.Name, SelectedShare.IsRecycleEnabled ? "enabled" : "disabled");
            EditErrorMessage = null;
            EditSuccessMessage = SelectedShare.IsRecycleEnabled
                ? Resources.Web_Share_RecycleEnabled
                : Resources.Web_Share_RecycleDisabled;

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error changing the recycle bin status");
            EditSuccessMessage = null;
            EditErrorMessage = Resources.Web_Error_ChangeStatusFailed;
            return false;
        }
    }

    // -- Recycle-bin limits --

    /// <summary>Global default size limit per recycle bin (0 = unlimited), shown for "inherit".</summary>
    public long GlobalRecycleMaxSizeBytes { get; private set; }

    public async Task<bool> SaveRecycleLimitsAsync(RecycleLimits limits)
    {
        if (SelectedShare is null) return false;

        if (!EnsureSelectedShareWritable())
            return false;

        // Recycle-bin behavior is a share feature setting → EditShareSettings (like the toggle).
        if (!await CanManageSelectedShareAsync(ManagementPermission.EditShareSettings))
        { EditSuccessMessage = null; EditErrorMessage = Resources.Web_Error_NoPermission; return false; }

        if (!limits.IsValid)
        {
            EditSuccessMessage = null;
            EditErrorMessage = Text("Web_Recycle_Error_Invalid",
                "Invalid limits: the maximum size must be at least 1 MB and the retention 1 to 3650 days.");
            return false;
        }

        try
        {
            SelectedShare.ApplyRecycleLimits(limits);
            await _shareRepo.UpdateAsync(SelectedShare);

            _logger.LogInformation("Recycle-bin limits of share '{ShareName}' changed to {Limits}",
                SelectedShare.Name, limits);
            EditErrorMessage = null;
            EditSuccessMessage = Text("Web_Recycle_Saved", "Recycle bin limits saved.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving the recycle-bin limits");
            EditSuccessMessage = null;
            EditErrorMessage = Resources.Web_Error_ChangeStatusFailed;
            return false;
        }
    }

    private static string Text(string key, string fallback)
        => Resources.ResourceManager.GetString(key) ?? fallback;

    public async Task<bool> ToggleShareHiddenAsync()
    {
        if (SelectedShare is null) return false;

        if (!EnsureSelectedShareWritable())
            return false;

        // Visibility (hidden = excluded from listings/ABE) is share-access control → ManageShareAccess.
        if (!await CanManageSelectedShareAsync(ManagementPermission.ManageShareAccess))
        { EditSuccessMessage = null; EditErrorMessage = Resources.Web_Error_NoPermission; return false; }

        try
        {
            SelectedShare.IsShareHidden = !SelectedShare.IsShareHidden;
            await _shareRepo.UpdateAsync(SelectedShare);

            _logger.LogInformation("Share '{ShareName}' visibility {Status}",
                SelectedShare.Name, SelectedShare.IsShareHidden ? "hidden" : "visible");
            EditErrorMessage = null;
            EditSuccessMessage = SelectedShare.IsShareHidden
                ? Resources.Web_Share_Enabled
                : Resources.Web_Share_Disabled;

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error changing the share visibility");
            EditSuccessMessage = null;
            EditErrorMessage = Resources.Web_Error_ChangeVisibilityFailed;
            return false;
        }
    }

    // -- Change department --

    /// <summary>
    /// Reassigns the selected share to <see cref="EditShareDepartmentId"/>. This is a
    /// pure DB field change (no filesystem move), so it needs no share lock or cloud
    /// lease. Explicit ACL entries are principal-keyed and untouched; what shifts is
    /// the department-default permission layer and which department-scoped admins may
    /// manage the share (see the confirm warning in ShareSettingsPanel).
    /// </summary>
    public async Task<bool> ChangeDepartmentAsync()
    {
        if (SelectedShare is null) return false;

        if (!EnsureSelectedShareWritable())
            return false;

        // Assigning the share's department is a share settings change → EditShareSettings.
        if (!await CanManageSelectedShareAsync(ManagementPermission.EditShareSettings))
        { EditSuccessMessage = null; EditErrorMessage = Resources.Web_Error_NoPermission; return false; }

        if (EditShareDepartmentId == SelectedShare.DepartmentId)
        {
            ShowDepartmentConfirm = false;
            EditErrorMessage = null;
            EditSuccessMessage = Resources.Web_Share_DepartmentUnchanged;
            return true;
        }

        try
        {
            SelectedShare.DepartmentId = EditShareDepartmentId;
            await _shareRepo.UpdateAsync(SelectedShare);

            _logger.LogInformation(
                "Share '{ShareName}' reassigned to department '{DepartmentId}'",
                SelectedShare.Name, EditShareDepartmentId);

            ShowDepartmentConfirm = false;
            EditErrorMessage = null;
            EditSuccessMessage = Resources.Web_Share_DepartmentChanged;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error changing the share department");
            EditSuccessMessage = null;
            EditErrorMessage = Resources.Web_Error_ChangeStatusFailed;
            return false;
        }
    }
}
