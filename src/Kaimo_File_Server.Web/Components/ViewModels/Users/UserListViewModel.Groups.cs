using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Language;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public partial class UserListViewModel
{
    // -- Group-Details --
    public List<User> GroupMembers { get; private set; } = [];

    // -- Group-Edit --
    public string EditGroupName { get; set; } = "";
    public List<CheckboxItem<User>> EditGroupMembers { get; private set; } = [];

    /// <summary>Ids of the users already in the edited group's department, captured when the edit
    /// starts. Drives the live "will be added to the department" warning below.</summary>
    private HashSet<Guid> _editGroupDeptMemberIds = [];

    /// <summary>Live warning shown while editing a department-scoped group's members: the selected
    /// users that are not yet in the group's department and so will be added to it on save. <c>null</c>
    /// when the group is Global or every selected member already belongs to the department.</summary>
    public string? EditGroupDeptWarning
    {
        get
        {
            if (SelectedGroup is null || SelectedGroup.DepartmentId == WellKnownGUIDs.DEPARTMENT_GLOBAL)
                return null;
            var missing = EditGroupMembers
                .Where(m => m.IsChecked && !_editGroupDeptMemberIds.Contains(m.Item.Id))
                .Select(m => m.Item.Name)
                .ToList();
            return missing.Count == 0
                ? null
                : string.Format(Resources.Web_Group_DeptWarning,
                    GetDepartmentName(SelectedGroup.DepartmentId), string.Join(", ", missing));
        }
    }

    // -- Create Group --
    public bool IsCreatingGroup { get; set; }
    public string CreateGroupName { get; set; } = "";
    public Guid CreateGroupDepartmentId { get; set; } = WellKnownGUIDs.DEPARTMENT_GLOBAL;

    // ══════════════════════════════════════════
    //  Edit Group
    // ══════════════════════════════════════════

    /// <summary>Whether the selected group may be edited at all. The Everyone group is a live
    /// mirror of every registered user (membership is maintained automatically from user
    /// creation to deletion), so it is never editable — neither its name nor its members.</summary>
    public bool SelectedGroupIsEditable => SelectedGroup is not null
        && SelectedGroup.Id != WellKnownGUIDs.GROUP_EVERYONE;

    public async Task StartEditGroupAsync()
    {
        if (SelectedGroup is null || _actorContext is null) return;

        if (!SelectedGroupIsEditable)
        {
            ErrorMessage = Resources.Web_Error_EveryoneNotEditable;
            return;
        }

        if (!await _mgmtAuth.CanManageGroupAsync(
                _actorContext, SelectedGroup.Id, ManagementPermission.ManageGroupMembers))
        {
            ErrorMessage = Resources.Web_Error_NoPermission;
            return;
        }

        IsEditing = true;
        ErrorMessage = null; SuccessMessage = null;

        EditGroupName = SelectedGroup.Name;

        var allUsers = (await _userRepo.GetAllAsync()).OrderBy(u => u.Name).ToList();
        var members = await _groupRepo.GetMembersAsync(SelectedGroup.Id);
        var memberIds = members.Select(u => u.Id).ToHashSet();
        EditGroupMembers = allUsers.Select(u => new CheckboxItem<User>(u, memberIds.Contains(u.Id))).ToList();

        // Capture the group's current department members so the edit UI can warn, live, which
        // selected users will be pulled into the department on save (see EditGroupDeptWarning).
        _editGroupDeptMemberIds = SelectedGroup.DepartmentId == WellKnownGUIDs.DEPARTMENT_GLOBAL
            ? []
            : (await _departmentRepo.GetUsersAsync(SelectedGroup.DepartmentId)).Select(u => u.Id).ToHashSet();
    }

    public async Task SaveGroupAsync()
    {
        if (SelectedGroup is null || _actorContext is null) return;

        if (!SelectedGroupIsEditable)
        {
            ErrorMessage = Resources.Web_Error_EveryoneNotEditable;
            return;
        }

        if (!await _mgmtAuth.CanManageGroupAsync(
                _actorContext, SelectedGroup.Id, ManagementPermission.ManageGroupMembers))
        {
            ErrorMessage = Resources.Web_Error_NoPermission;
            return;
        }

        if (string.IsNullOrWhiteSpace(EditGroupName))
        {
            ErrorMessage = Resources.Web_Group_NameRequired;
            return;
        }

        // Admins membership mirrors the global admin role, so emptying it of enabled
        // members would strip the last admin. Block that, mirroring the last-admin guard.
        if (SelectedGroup.Id == WellKnownGUIDs.GROUP_ADMINS
            && !EditGroupMembers.Any(m => m.IsChecked && m.Item.IsEnabled))
        {
            ErrorMessage = Resources.Web_Error_LastAdmin;
            return;
        }

        try
        {
            IsSaving = true;
            ErrorMessage = null;

            // Group.Name is init-only, so a rename is applied by persisting a fresh
            // aggregate carrying the same identity and department (mirrors role rename).
            if (EditGroupName.Trim() != SelectedGroup.Name)
            {
                var renamed = new Group(SelectedGroup.Id, EditGroupName.Trim(), SelectedGroup.DepartmentId);
                await _groupRepo.UpdateAsync(renamed);
                SelectedGroup = renamed;

                var idx = AllGroups.FindIndex(g => g.Id == renamed.Id);
                if (idx >= 0) AllGroups[idx] = renamed;
                AllGroups = AllGroups.OrderBy(g => g.Name).ToList();
            }

            var selectedUserIds = EditGroupMembers.Where(m => m.IsChecked).Select(m => m.Item.Id).ToList();

            if (SelectedGroup.Id == WellKnownGUIDs.GROUP_ADMINS)
            {
                // Full-mirror: every membership change here grants/revokes the admin role.
                var previousMemberIds = (await _groupRepo.GetMembersAsync(SelectedGroup.Id))
                    .Select(u => u.Id).ToHashSet();
                await _groupRepo.SetMembersAsync(SelectedGroup.Id, selectedUserIds);

                foreach (var uid in selectedUserIds)
                    await ReconcileAdminCouplingAsync(uid, true);
                foreach (var uid in previousMemberIds.Where(id => !selectedUserIds.Contains(id)))
                    await ReconcileAdminCouplingAsync(uid, false);
            }
            else
            {
                await _groupRepo.SetMembersAsync(SelectedGroup.Id, selectedUserIds);
            }

            // A member of a department-scoped group must also belong to that department —
            // otherwise the membership is orphaned: it would not surface on the user's own
            // record, and the user's next profile save would silently drop it (the group is
            // filtered out of the department-scoped picker). So grant the group's department
            // to every selected member that lacks it, and surface who was affected.
            var addedToDept = new List<string>();
            if (SelectedGroup.DepartmentId != WellKnownGUIDs.DEPARTMENT_GLOBAL)
            {
                var deptMemberIds = (await _departmentRepo.GetUsersAsync(SelectedGroup.DepartmentId))
                    .Select(u => u.Id).ToHashSet();
                foreach (var member in EditGroupMembers
                             .Where(m => m.IsChecked && !deptMemberIds.Contains(m.Item.Id)))
                {
                    await _departmentRepo.AddUserAsync(SelectedGroup.DepartmentId, member.Item.Id);
                    addedToDept.Add(member.Item.Name);
                }
            }

            GroupMembers = (await _groupRepo.GetMembersAsync(SelectedGroup.Id)).OrderBy(u => u.Name).ToList();

            IsEditing = false;
            SuccessMessage = addedToDept.Count == 0
                ? Resources.Web_Members_Saved
                : $"{Resources.Web_Members_Saved} {string.Format(Resources.Web_Group_MembersAddedToDept, GetDepartmentName(SelectedGroup.DepartmentId), string.Join(", ", addedToDept))}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving group {GroupId}", SelectedGroup.Id);
            ErrorMessage = Resources.Web_Error_SaveFailed;
        }
        finally { IsSaving = false; }
    }

    // ══════════════════════════════════════════
    //  Create Group (with Department selector)
    // ══════════════════════════════════════════

    public void StartCreateGroup()
    {
        if (!CanManageGroups) { ErrorMessage = Resources.Web_Error_NoPermission; return; }
        CancelEdit();
        SelectedUser = null; SelectedGroup = null; SelectedRole = null;
        IsCreatingGroup = true; IsCreatingUser = false; IsCreatingRole = false;
        CreateGroupName = "";
        CreateGroupDepartmentId = WellKnownGUIDs.DEPARTMENT_GLOBAL;
        ErrorMessage = null; SuccessMessage = null;
    }

    public async Task CreateGroupAsync()
    {
        ErrorMessage = null;
        if (string.IsNullOrWhiteSpace(CreateGroupName)) { ErrorMessage = Resources.Web_Group_NameRequired; return; }
        if (_actorContext is null) return;

        if (!await _mgmtAuth.HasAnyPermissionAsync(_actorContext, ManagementPermission.CreateGroups))
        { ErrorMessage = Resources.Web_Error_NoPermission; return; }

        try
        {
            IsSaving = true;
            var group = new Group(Guid.NewGuid(), CreateGroupName.Trim(), CreateGroupDepartmentId);
            await _groupRepo.CreateAsync(group);
            IsCreatingGroup = false; CreateGroupName = "";
            await LoadTabDataAsync();
            SuccessMessage = Resources.Web_Group_Created;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating the group");
            ErrorMessage = Resources.Web_Error_CreateFailed;
        }
        finally { IsSaving = false; }
    }
}
