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

    private static string GroupGrantsUnassignableRolesMessage
        => Resources.ResourceManager.GetString("Web_Error_GroupGrantsUnassignableRoles")
           ?? "This group grants roles you are not allowed to assign, so you cannot add members to it.";

    /// <summary>The members of the selected group's department, or <c>null</c> for a Global group,
    /// which takes everyone. A department group only takes members of exactly its department;
    /// joining it never adds the user to the department.</summary>
    private async Task<HashSet<Guid>?> SelectedGroupDepartmentMemberIdsAsync()
        => SelectedGroup!.DepartmentId == WellKnownGUIDs.DEPARTMENT_GLOBAL
            ? null
            : (await _departmentRepo.GetUsersAsync(SelectedGroup.DepartmentId)).Select(u => u.Id).ToHashSet();

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
        var memberIds = (await _groupRepo.GetMembersAsync(SelectedGroup.Id)).Select(u => u.Id).ToHashSet();

        // Offer the current members plus the department's members (everyone for a Global group,
        // which only global actors manage).
        var departmentMemberIds = await SelectedGroupDepartmentMemberIdsAsync();
        EditGroupMembers = allUsers
            .Where(u => memberIds.Contains(u.Id) || departmentMemberIds is null || departmentMemberIds.Contains(u.Id))
            .Select(u => new CheckboxItem<User>(u, memberIds.Contains(u.Id)))
            .ToList();
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

        // Joining grants the group's roles: adding members needs the right to assign them.
        var previousIds = (await _groupRepo.GetMembersAsync(SelectedGroup.Id)).Select(u => u.Id).ToHashSet();
        var joining = EditGroupMembers.Where(m => m.IsChecked && !previousIds.Contains(m.Item.Id))
            .Select(m => m.Item).DistinctBy(u => u.Id).ToList();
        if (joining.Count > 0
            && !await _roleAssignments.CanAddGroupMembersAsync(_actorContext, SelectedGroup.Id))
        {
            ErrorMessage = GroupGrantsUnassignableRolesMessage;
            return;
        }

        // Leaving revokes the group's roles: bounded by the same authority as assigning them.
        var selectedIds = EditGroupMembers.Where(m => m.IsChecked).Select(m => m.Item.Id).ToHashSet();
        if (previousIds.Any(id => !selectedIds.Contains(id))
            && !await _roleAssignments.CanRemoveGroupMembersAsync(_actorContext, SelectedGroup.Id))
        {
            ErrorMessage = GroupGrantsUnrevokableRolesMessage;
            return;
        }

        // A department group only takes members of its department. Read again: the set loaded
        // with the editor may be stale.
        var deptMemberIds = joining.Count == 0 ? null : await SelectedGroupDepartmentMemberIdsAsync();
        var outsiders = deptMemberIds is null ? [] : joining.Where(u => !deptMemberIds.Contains(u.Id)).ToList();
        if (outsiders.Count > 0)
        {
            ErrorMessage = string.Format(Resources.Web_Group_MembersNotInDepartment,
                GetDepartmentName(SelectedGroup.DepartmentId), string.Join(", ", outsiders.Select(u => u.Name)));
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
                var previousMemberIds = previousIds;
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

            GroupMembers = (await _groupRepo.GetMembersAsync(SelectedGroup.Id)).OrderBy(u => u.Name).ToList();

            IsEditing = false;
            SuccessMessage = Resources.Web_Members_Saved;
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
        CreateGroupDepartmentId = AuthorizedDepartmentsForGroupCreate.FirstOrDefault()?.Id
                                  ?? WellKnownGUIDs.DEPARTMENT_GLOBAL;
        ErrorMessage = null; SuccessMessage = null;
    }

    public async Task CreateGroupAsync()
    {
        ErrorMessage = null;
        if (string.IsNullOrWhiteSpace(CreateGroupName)) { ErrorMessage = Resources.Web_Group_NameRequired; return; }
        if (_actorContext is null) return;

        // The group's department decides who manages it, so it must lie in the actor's scope;
        // a Global group needs global authority.
        if (!await _mgmtAuth.CanManageDepartmentAsync(
                _actorContext, CreateGroupDepartmentId, ManagementPermission.CreateGroups))
        { ErrorMessage = Resources.Web_Dept_NoPermissionInDept; return; }

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
