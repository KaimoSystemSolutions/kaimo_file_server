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
    private HashSet<Guid> _editGroupMemberIds = [];

    private static string GroupGrantsUnassignableRolesMessage
        => Resources.ResourceManager.GetString("Web_Error_GroupGrantsUnassignableRoles")
           ?? "This group grants roles you are not allowed to assign, so you cannot add members to it.";

    private static string GroupMembersNotPermittedFormat
        => Resources.ResourceManager.GetString("Web_Group_MembersNotPermitted")
           ?? "You are not allowed to add the following users to the department of this group: {0}";

    /// <summary>Live warning shown while editing a department-scoped group's members: the newly
    /// selected users that are not yet in the group's department and so will be added to it on save.
    /// <c>null</c> when the group is Global or every joining member already belongs to the department.</summary>
    public string? EditGroupDeptWarning
    {
        get
        {
            if (SelectedGroup is null || SelectedGroup.DepartmentId == WellKnownGUIDs.DEPARTMENT_GLOBAL)
                return null;
            var missing = EditGroupMembers
                .Where(m => m.IsChecked && !_editGroupMemberIds.Contains(m.Item.Id)
                            && !_editGroupDeptMemberIds.Contains(m.Item.Id))
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
        _editGroupMemberIds = memberIds;

        // Capture the group's current department members so the edit UI can warn, live, which
        // selected users will be pulled into the department on save (see EditGroupDeptWarning).
        _editGroupDeptMemberIds = SelectedGroup.DepartmentId == WellKnownGUIDs.DEPARTMENT_GLOBAL
            ? []
            : (await _departmentRepo.GetUsersAsync(SelectedGroup.DepartmentId)).Select(u => u.Id).ToHashSet();

        // Offer current members, the department's members, and every user the actor may pull
        // into the group's department — never a user outside the actor's authority. Only users
        // within the actor's reach can be pulled in, so the full check runs for those only.
        using var evaluation = _mgmtAuth.BeginEvaluationScope(); // read-only batch of checks
        var candidateIds = SelectedGroup.DepartmentId == WellKnownGUIDs.DEPARTMENT_GLOBAL
            ? null
            : await _mgmtAuth.GetUserIdsInScopeAsync(_actorContext, ManagementPermission.AssignDepartments);
        EditGroupMembers = [];
        foreach (var user in allUsers)
        {
            var isMember = memberIds.Contains(user.Id);
            if (isMember
                || ((candidateIds is null || candidateIds.Contains(user.Id) || _editGroupDeptMemberIds.Contains(user.Id))
                    && await CanJoinSelectedGroupDepartmentAsync(user.Id, _editGroupDeptMemberIds)))
                EditGroupMembers.Add(new CheckboxItem<User>(user, isMember));
        }
    }

    /// <summary>Whether <paramref name="userId"/> may become a member of the selected group with
    /// respect to its department: always for a Global group (only global actors manage those) and
    /// for department members (<paramref name="departmentMemberIds"/>, loaded by the caller);
    /// otherwise the actor must be allowed to add the user there.</summary>
    private async Task<bool> CanJoinSelectedGroupDepartmentAsync(Guid userId, HashSet<Guid> departmentMemberIds)
        => SelectedGroup!.DepartmentId == WellKnownGUIDs.DEPARTMENT_GLOBAL
           || departmentMemberIds.Contains(userId)
           || await _mgmtAuth.CanAddUserToDepartmentAsync(_actorContext!, userId, SelectedGroup.DepartmentId);

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

        // Joining a department group also adds the user to that department (below), so it is
        // bounded by the same authority as adding the user to the department directly. The
        // department's members are read again: the set loaded with the editor may be stale.
        var deptMemberIds = SelectedGroup.DepartmentId == WellKnownGUIDs.DEPARTMENT_GLOBAL || joining.Count == 0
            ? []
            : (await _departmentRepo.GetUsersAsync(SelectedGroup.DepartmentId)).Select(u => u.Id).ToHashSet();
        var notPermitted = new List<string>();
        foreach (var user in joining)
            if (!await CanJoinSelectedGroupDepartmentAsync(user.Id, deptMemberIds))
                notPermitted.Add(user.Name);
        if (notPermitted.Count > 0)
        {
            ErrorMessage = string.Format(GroupMembersNotPermittedFormat, string.Join(", ", notPermitted));
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

            // A member of a department-scoped group must also belong to that department —
            // otherwise the membership is orphaned: it would not surface on the user's own
            // record (the group is filtered out of the department-scoped picker). So grant the
            // group's department to every joining member that lacks it, and surface who was affected.
            var addedToDept = new List<string>();
            if (SelectedGroup.DepartmentId != WellKnownGUIDs.DEPARTMENT_GLOBAL)
            {
                // Only joining members, whose department authority was checked above; an existing
                // member outside the department (e.g. the group was moved here) stays outside.
                foreach (var user in joining.Where(u => !deptMemberIds.Contains(u.Id)))
                {
                    await _departmentRepo.AddUserAsync(SelectedGroup.DepartmentId, user.Id);
                    addedToDept.Add(user.Name);
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
