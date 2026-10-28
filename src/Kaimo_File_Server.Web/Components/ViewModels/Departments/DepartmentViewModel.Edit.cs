using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Web.DynamicHelpers;
using Kaimo_File_Server.Core.Language;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public partial class DepartmentViewModel
{
    // -- Edit --
    public string EditName { get; set; } = "";
    public string EditDescription { get; set; } = "";
    public Guid? EditParentId { get; set; }
    public bool EditHasOwnPermission { get; set; }
    public FilePermission EditDefaultPermission { get; set; } = FilePermission.None;
    /// <summary>When false the department uses its automatic color (<see cref="Department.Color"/> = null).</summary>
    public bool EditUseCustomColor { get; set; }
    /// <summary>Current picker value; only persisted when <see cref="EditUseCustomColor"/> is true.</summary>
    public string EditColor { get; set; } = "";
    public List<CheckboxItem<User>> EditMembers { get; private set; } = [];
    public List<CheckboxItem<Group>> EditGroups { get; private set; } = [];
    public List<CheckboxItem<ShareDefinition>> EditShares { get; private set; } = [];
    public List<Department> AvailableParents { get; private set; } = [];

    // ══════════════════════════════════════════
    //  Edit
    // ══════════════════════════════════════════

    public async Task StartEditAsync()
    {
        if (Selected is null || _actorContext is null) return;

        if (!await _mgmtAuth.CanManageDepartmentAsync(
                _actorContext, Selected.Id, ManagementPermission.EditDepartment))
        {
            ErrorMessage = Resources.Web_Dept_NoPermissionEdit;
            return;
        }

        IsEditing = true;
        ErrorMessage = null; SuccessMessage = null;

        EditName = Selected.Name;
        EditDescription = Selected.Description ?? "";
        EditParentId = Selected.ParentDepartmentId;

        EditHasOwnPermission = Selected.DefaultFilePermission.HasValue;
        EditDefaultPermission = Selected.DefaultFilePermission.HasValue
            ? (FilePermission)Selected.DefaultFilePermission.Value
            : FilePermission.None;

        // Seed the picker with the current automatic color so enabling the toggle starts sensibly.
        EditUseCustomColor = Selected.Color != null;
        EditColor = Selected.Color ?? DepartmentPalette.AutoHex(Selected.Id);

        // Available parents: authorized ones only, never self or a descendant (cycles). Global is
        // not a parent option: "no parent" already means "directly below Global".
        AvailableParents = await LoadAvailableParentsAsync(Selected);

        // Members (M:N — DepartmentUser), groups (Group.DepartmentId) and shares
        // (ShareDefinition.DepartmentId): each picker offers what already belongs here plus what
        // the actor controls and may move in (see DepartmentViewModel.EditScope.cs). System groups
        // (Everyone, Admins) and the home-folder share stay permanently Global.
        var allUsers = (await _userRepo.GetAllAsync()).OrderBy(u => u.Name).ToList();
        EditMembers = await BuildMemberPickerAsync(Selected.Id, allUsers);

        var allGroups = (await _groupRepo.GetAllAsync()).OrderBy(g => g.Name).ToList();
        EditGroups = await BuildGroupPickerAsync(Selected.Id, allGroups);

        var allShares = (await _shareRepo.GetAllAsync()).OrderBy(s => s.Name).ToList();
        EditShares = await BuildSharePickerAsync(Selected.Id, allShares);
    }

    public async Task SaveAsync()
    {
        if (Selected is null || _actorContext is null) return;

        if (!await _mgmtAuth.CanManageDepartmentAsync(
                _actorContext, Selected.Id, ManagementPermission.EditDepartment))
        {
            ErrorMessage = Resources.Web_Error_NoPermission;
            return;
        }

        try
        {
            IsSaving = true;
            ErrorMessage = null;

            // Checked before anything is changed, so a rejected rename leaves the edit form intact.
            if (!string.IsNullOrWhiteSpace(EditName)
                && await IsNameTakenAsync(EditName.Trim(), Selected.Id))
            {
                ErrorMessage = Resources.Web_Dept_NameTaken;
                return;
            }

            // -- Parent change: validated (no cycles) and authorized on the old and new parent --
            var isGlobal = IsGlobalDepartment(Selected);
            var newParentId = isGlobal || EditParentId == WellKnownGUIDs.DEPARTMENT_GLOBAL ? null : EditParentId;
            var oldParentId = (await _departmentRepo.GetByIdAsync(Selected.Id))?.ParentDepartmentId;
            if (newParentId != oldParentId
                && await ValidateParentChangeAsync(Selected.Id, oldParentId, newParentId) is { } parentError)
            {
                ErrorMessage = parentError;
                return;
            }

            // -- Pending assignment diff, authorized as a whole before anything is written --
            var desiredUserIds = EditMembers
                .Where(m => m.IsChecked).Select(m => m.Item.Id).ToHashSet();
            var currentUserIds = Members.Select(u => u.Id).ToHashSet();
            var addedUsers = EditMembers
                .Where(m => m.IsChecked && !currentUserIds.Contains(m.Item.Id))
                .Select(m => m.Item).DistinctBy(u => u.Id).ToList();
            var removedUserIds = currentUserIds.Except(desiredUserIds).ToList();

            var movedGroups = await ResolveMovedGroupsAsync(Selected.Id);
            var movedShares = await ResolveMovedSharesAsync(Selected.Id);

            var refused = await FindRefusedChangesAsync(Selected.Id, addedUsers, removedUserIds,
                movedGroups.Select(m => m.Group), movedShares.Select(m => m.Share));
            if (refused.Count > 0)
            {
                ErrorMessage = string.Format(ChangesNotPermittedFormat, string.Join(", ", refused));
                return;
            }

            // -- Update department entity --
            if (!string.IsNullOrWhiteSpace(EditName))
                Selected.Name = EditName.Trim();
            Selected.Description = string.IsNullOrWhiteSpace(EditDescription) ? null : EditDescription.Trim();
            // The Global department is a pure placeholder for "the whole system": it has no
            // parent and never carries default file permissions.
            Selected.ParentDepartmentId = newParentId;
            Selected.DefaultFilePermission = EditHasOwnPermission && !isGlobal
                ? (long)EditDefaultPermission
                : null;
            // Normalize guards the trust boundary: this value is emitted into inline CSS.
            Selected.Color = EditUseCustomColor ? DepartmentPalette.Normalize(EditColor) : null;

            await _departmentRepo.UpdateAsync(Selected);

            // -- Members diff (M:N via DepartmentUser) --
            foreach (var user in addedUsers)
                await _departmentRepo.AddUserAsync(Selected.Id, user.Id);
            var removedGroupMemberships = 0;
            foreach (var userId in removedUserIds)
            {
                await _departmentRepo.RemoveUserAsync(Selected.Id, userId);

                var userGroups = await _userRepo.GetGroupsForUserAsync(userId);
                var remainingGroupIds = userGroups
                    .Where(group => group.DepartmentId != Selected.Id)
                    .Select(group => group.Id)
                    .ToList();

                if (remainingGroupIds.Count != userGroups.Count)
                {
                    await _userRepo.SetGroupsForUserAsync(userId, remainingGroupIds);
                    removedGroupMemberships += userGroups.Count - remainingGroupIds.Count;
                }
            }

            // -- Groups diff (direct FK: Group.DepartmentId); released ones go back to Global --
            foreach (var (group, shouldBelong) in movedGroups)
            {
                group.DepartmentId = shouldBelong ? Selected.Id : WellKnownGUIDs.DEPARTMENT_GLOBAL;
                await _groupRepo.UpdateAsync(group);
            }

            // -- Shares diff (direct FK: ShareDefinition.DepartmentId) --
            foreach (var (share, shouldBelong) in movedShares)
            {
                share.DepartmentId = shouldBelong ? Selected.Id : WellKnownGUIDs.DEPARTMENT_GLOBAL;
                await _shareRepo.UpdateAsync(share);
            }

            // -- Refresh --
            var refreshed = await _departmentRepo.GetByIdAsync(Selected.Id);
            if (refreshed != null) Selected = refreshed;

            await LoadDetailAsync(Selected);
            await LoadDepartmentsAsync();

            IsEditing = false;
            SuccessMessage = removedGroupMemberships == 0
                ? Resources.Web_Dept_Saved
                : $"{Resources.Web_Dept_Saved} {string.Format(Resources.Web_Dept_GroupMembershipsRemoved, removedGroupMemberships)}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving department {Id}", Selected.Id);
            ErrorMessage = Resources.Web_Error_SaveFailed;
        }
        finally { IsSaving = false; }
    }
}
