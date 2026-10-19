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

        // Available parents (exclude self + descendants to prevent cycles). Global is not a
        // parent option: "no parent" already means "directly below Global".
        var allDepts = await _departmentRepo.GetAllAsync();
        var descendantIds = await _departmentRepo.GetDescendantIdsAsync(Selected.Id);
        descendantIds.Add(Selected.Id);
        descendantIds.Add(WellKnownGUIDs.DEPARTMENT_GLOBAL);
        AvailableParents = allDepts
            .Where(d => !descendantIds.Contains(d.Id))
            .OrderByGlobalFirst().ToList();

        // Members (M:N — DepartmentUser)
        var allUsers = (await _userRepo.GetAllAsync()).OrderBy(u => u.Name).ToList();
        var memberIds = Members.Select(u => u.Id).ToHashSet();
        EditMembers = allUsers
            .Select(u => new CheckboxItem<User>(u, memberIds.Contains(u.Id))).ToList();

        // Groups (direct FK: Group.DepartmentId). The Everyone group is a live mirror of every
        // registered user and stays permanently Global — it must never be pulled into a
        // department, so it is excluded from the assignable list.
        var allGroups = (await _groupRepo.GetAllAsync()).OrderBy(g => g.Name).ToList();
        EditGroups = allGroups
            .Where(g => g.Id != WellKnownGUIDs.GROUP_EVERYONE)
            .Select(g => new CheckboxItem<Group>(g, g.DepartmentId == Selected.Id)).ToList();

        // Shares (direct FK: ShareDefinition.DepartmentId)
        var allShares = (await _shareRepo.GetAllAsync()).OrderBy(s => s.Name).ToList();
        EditShares = allShares
            .Select(s => new CheckboxItem<ShareDefinition>(s, s.DepartmentId == Selected.Id)).ToList();
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

            // -- Update department entity --
            if (!string.IsNullOrWhiteSpace(EditName))
                Selected.Name = EditName.Trim();
            Selected.Description = string.IsNullOrWhiteSpace(EditDescription) ? null : EditDescription.Trim();
            // The Global department is a pure placeholder for "the whole system": it has no
            // parent and never carries default file permissions.
            var isGlobal = IsGlobalDepartment(Selected);
            Selected.ParentDepartmentId = isGlobal || EditParentId == WellKnownGUIDs.DEPARTMENT_GLOBAL
                ? null : EditParentId;
            Selected.DefaultFilePermission = EditHasOwnPermission && !isGlobal
                ? (long)EditDefaultPermission
                : null;
            // Normalize guards the trust boundary: this value is emitted into inline CSS.
            Selected.Color = EditUseCustomColor ? DepartmentPalette.Normalize(EditColor) : null;

            await _departmentRepo.UpdateAsync(Selected);

            // -- Members diff (M:N via DepartmentUser) --
            var desiredUserIds = EditMembers
                .Where(m => m.IsChecked).Select(m => m.Item.Id).ToHashSet();
            var currentUserIds = Members.Select(u => u.Id).ToHashSet();

            foreach (var userId in desiredUserIds.Except(currentUserIds))
                await _departmentRepo.AddUserAsync(Selected.Id, userId);
            var removedGroupMemberships = 0;
            foreach (var userId in currentUserIds.Except(desiredUserIds))
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

            // -- Groups diff (direct FK: Group.DepartmentId) --
            foreach (var item in EditGroups)
            {
                var group = item.Item;
                var shouldBelong = item.IsChecked;
                var currentlyBelongs = group.DepartmentId == Selected.Id;

                if (shouldBelong && !currentlyBelongs)
                {
                    group.DepartmentId = Selected.Id;
                    await _groupRepo.UpdateAsync(group);
                }
                else if (!shouldBelong && currentlyBelongs)
                {
                    group.DepartmentId = WellKnownGUIDs.DEPARTMENT_GLOBAL;
                    await _groupRepo.UpdateAsync(group);
                }
            }

            // -- Shares diff (direct FK: ShareDefinition.DepartmentId) --
            foreach (var item in EditShares)
            {
                var share = item.Item;
                var shouldBelong = item.IsChecked;
                var currentlyBelongs = share.DepartmentId == Selected.Id;

                if (shouldBelong && !currentlyBelongs)
                {
                    share.DepartmentId = Selected.Id;
                    await _shareRepo.UpdateAsync(share);
                }
                else if (!shouldBelong && currentlyBelongs)
                {
                    share.DepartmentId = WellKnownGUIDs.DEPARTMENT_GLOBAL;
                    await _shareRepo.UpdateAsync(share);
                }
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
