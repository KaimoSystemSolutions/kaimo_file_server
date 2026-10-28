using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Language;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public partial class UserListViewModel
{
    // -- Delete Confirmation --
    public bool IsConfirmingDelete { get; set; }

    // ══════════════════════════════════════════
    //  Delete
    // ══════════════════════════════════════════

    public void RequestDeleteUser() { IsConfirmingDelete = true; ErrorMessage = null; }
    public void RequestDeleteGroup() { IsConfirmingDelete = true; ErrorMessage = null; }
    public void RequestDeleteRole() { IsConfirmingDelete = true; ErrorMessage = null; }

    public async Task ConfirmDeleteUserAsync()
    {
        if (SelectedUser is null || _actorContext is null) return;
        if (!await _mgmtAuth.CanManageUserAsync(_actorContext, SelectedUser.Id, ManagementPermission.DeleteUsers))
        { ErrorMessage = Resources.Web_Error_NoPermission; return; }

        // Last-admin protection: deleting the user removes their assignments and group
        // memberships too, so refuse if this user is the sole enabled global admin.
        var adminUserIds = await GetEnabledGlobalAdminUserIdsAsync();
        if (adminUserIds.Count == 1 && adminUserIds.Contains(SelectedUser.Id))
        { ErrorMessage = Resources.Web_Error_LastAdmin; return; }

        try
        {
            IsSaving = true;
            await _userRepo.DeleteAsync(SelectedUser.Id);
            InvalidateCachedWebDavLogins(SelectedUser.Id);
            SelectedUser = null; IsConfirmingDelete = false;
            await LoadTabDataAsync();
            SuccessMessage = Resources.Web_User_Deleted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting");
            ErrorMessage = Resources.Web_Error_DeleteFailed;
        }
        finally { IsSaving = false; }
    }

    public async Task ConfirmDeleteGroupAsync()
    {
        if (SelectedGroup is null || _actorContext is null) return;
        if (WellKnownGUIDs.PROTECTED_GROUPS.Contains(SelectedGroup.Id))
        { ErrorMessage = Resources.Web_Error_SystemGroupUndeletable; return; }
        if (!await _mgmtAuth.CanManageGroupAsync(_actorContext, SelectedGroup.Id, ManagementPermission.DeleteGroups))
        { ErrorMessage = Resources.Web_Error_NoPermission; return; }

        try
        {
            IsSaving = true;
            await _groupRepo.DeleteAsync(SelectedGroup.Id);
            SelectedGroup = null; IsConfirmingDelete = false;
            await LoadTabDataAsync();
            SuccessMessage = Resources.Web_Group_Deleted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting");
            ErrorMessage = Resources.Web_Error_DeleteFailed;
        }
        finally { IsSaving = false; }
    }

    public async Task ConfirmDeleteRoleAsync()
    {
        if (SelectedRole is null || _actorContext is null) return;

        try
        {
            IsSaving = true;
            // System-role guard, global authority over every bit, last-admin guard and the
            // removal of the role's assignments live in the shared service.
            var role = await _roleRepo.GetByIdAsync(SelectedRole.Id) ?? SelectedRole;
            var error = await _roleAssignments.DeleteRoleAsync(_actorContext, role);
            if (error is not null) { ErrorMessage = error; return; }
            SelectedRole = null; RoleScopedAssignments = [];
            IsConfirmingDelete = false;
            await LoadTabDataAsync();
            SuccessMessage = Resources.Web_Role_Deleted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting");
            ErrorMessage = Resources.Web_Error_DeleteFailed;
        }
        finally { IsSaving = false; }
    }
}
