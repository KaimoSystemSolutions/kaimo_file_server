using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Security;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>
/// Group memberships edited from the user form. Only groups the actor may assign users to
/// (<see cref="ManagementPermission.AssignGroups"/> at the group's department) are offered and
/// changed; every other membership of the user stays untouched. Joining or leaving a group grants
/// or revokes the roles it carries, so both are additionally bounded by the actor's authority to
/// assign those roles. Leaving a department, which drops the user's groups there, is bounded the same way.
/// </summary>
public partial class UserListViewModel
{
    private static string GroupGrantsUnrevokableRolesMessage
        => Resources.ResourceManager.GetString("Web_Error_GroupGrantsUnrevokableRoles")
           ?? "This group grants roles you are not allowed to revoke, so you cannot remove members from it.";

    private static string ChangesNotPermittedFormat
        => Resources.ResourceManager.GetString("Web_Dept_ChangesNotPermitted")
           ?? "You are not allowed to make the following changes: {0}";

    private Task<bool> CanAssignUsersToGroupAsync(Guid groupId)
        => _mgmtAuth.CanManageGroupAsync(_actorContext!, groupId, ManagementPermission.AssignGroups);

    /// <summary>The candidate groups the actor may assign users to, pre-checked for current memberships.</summary>
    private async Task<List<CheckboxItem<Group>>> BuildUserGroupPickerAsync(
        IEnumerable<Group> candidates, HashSet<Guid> memberGroupIds)
    {
        using var evaluation = _mgmtAuth.BeginEvaluationScope(); // read-only batch of checks
        var items = new List<CheckboxItem<Group>>();
        foreach (var group in candidates)
            if (await CanAssignUsersToGroupAsync(group.Id))
                items.Add(new CheckboxItem<Group>(group, memberGroupIds.Contains(group.Id)));
        return items;
    }

    /// <summary>
    /// Authorizes the form's membership changes before anything is written. Group memberships
    /// are only changed with AssignGroups over the user (<c>CanAssignGroups</c>); leaving a
    /// department drops the user's groups there, bounded like leaving them directly.
    /// </summary>
    private async Task<(bool CanAssignGroups, List<Group> Joined, List<Group> Left, string? Error)>
        AuthorizeMembershipChangesAsync(Guid userId)
    {
        var canAssignGroups = await _mgmtAuth.CanManageUserAsync(_actorContext!, userId, ManagementPermission.AssignGroups);
        var storedGroupIds = (await _userRepo.GetGroupsForUserAsync(userId)).Select(g => g.Id).ToHashSet();
        var (joined, left) = PendingGroupChanges(storedGroupIds);

        var error = canAssignGroups ? await AuthorizeGroupChangesAsync(joined, left) : null;
        if (error is null && CanAssignDepartments)
            error = await AuthorizeDepartmentLeavesAsync(userId);
        return (canAssignGroups, joined, left, error);
    }

    /// <summary>The groups the form joins and leaves, compared with the stored memberships.</summary>
    private (List<Group> Joined, List<Group> Left) PendingGroupChanges(HashSet<Guid> storedGroupIds)
    {
        var items = EditUserGroups.DistinctBy(g => g.Item.Id).ToList();
        return (
            items.Where(g => g.IsChecked && !storedGroupIds.Contains(g.Item.Id)).Select(g => g.Item).ToList(),
            items.Where(g => !g.IsChecked && storedGroupIds.Contains(g.Item.Id)).Select(g => g.Item).ToList());
    }

    /// <summary>
    /// Authorizes the pending membership changes before anything is written. Returns a localized
    /// error or <c>null</c> when every change is permitted.
    /// </summary>
    private async Task<string?> AuthorizeGroupChangesAsync(List<Group> joined, List<Group> left)
    {
        var refused = new List<string>();
        foreach (var group in joined.Concat(left))
            if (!await CanAssignUsersToGroupAsync(group.Id))
                refused.Add(group.Name);
        if (refused.Count > 0)
            return string.Format(ChangesNotPermittedFormat, string.Join(", ", refused));

        foreach (var group in joined)
            if (!await _roleAssignments.CanAddGroupMembersAsync(_actorContext!, group.Id))
                return GroupGrantsUnassignableRolesMessage;

        foreach (var group in left)
            if (!await _roleAssignments.CanRemoveGroupMembersAsync(_actorContext!, group.Id))
                return GroupGrantsUnrevokableRolesMessage;

        return null;
    }

    /// <summary>
    /// Leaving a department drops the user's memberships in its groups, so every department the
    /// form unchecks is bounded like leaving those groups directly. Returns a localized error or
    /// <c>null</c> when every pending leave is permitted.
    /// </summary>
    private async Task<string?> AuthorizeDepartmentLeavesAsync(Guid userId)
    {
        var shownIds = EditUserDepartments.Select(d => d.Item.Id).ToHashSet();
        var desiredIds = EditUserDepartments.Where(d => d.IsChecked).Select(d => d.Item.Id).ToHashSet();
        var leftIds = UserDepartments.Select(d => d.Id).Where(id => shownIds.Contains(id) && !desiredIds.Contains(id));

        foreach (var departmentId in leftIds)
            if (!await _roleAssignments.CanLeaveDepartmentGroupsAsync(_actorContext!, userId, departmentId))
                return GroupGrantsUnrevokableRolesMessage;

        return null;
    }

    /// <summary>
    /// Applies the department picker — diffed against its authorized subset only, so memberships in
    /// departments the actor cannot manage are never touched. Each change is still gated per
    /// department by AssignDepartments (adding also needs authority over the user, see
    /// IManagementAuthService.CanAddUserToDepartmentAsync). Leaving a department drops the group
    /// memberships tied to it (authorized beforehand by <see cref="AuthorizeDepartmentLeavesAsync"/>).
    /// Returns the user's departments afterwards.
    /// </summary>
    private async Task<HashSet<Guid>> ApplyDepartmentChangesAsync(Guid userId)
    {
        var currentIds = UserDepartments.Select(d => d.Id).ToHashSet();
        if (!CanAssignDepartments)
            return currentIds;

        var shownIds = EditUserDepartments.Select(d => d.Item.Id).ToHashSet();
        var desiredIds = EditUserDepartments.Where(d => d.IsChecked).Select(d => d.Item.Id).ToHashSet();

        foreach (var departmentId in desiredIds.Except(currentIds))
            if (await _mgmtAuth.CanAddUserToDepartmentAsync(_actorContext!, userId, departmentId))
                await _departmentRepo.AddUserAsync(departmentId, userId);

        foreach (var departmentId in currentIds.Intersect(shownIds).Except(desiredIds))
            if (await _mgmtAuth.CanManageDepartmentAsync(_actorContext!, departmentId, ManagementPermission.AssignDepartments))
            {
                await _departmentRepo.RemoveUserAsync(departmentId, userId);
                // Mirror DepartmentViewModel: leaving a department drops group memberships tied to it.
                var groups = await _userRepo.GetGroupsForUserAsync(userId);
                var remaining = groups.Where(g => g.DepartmentId != departmentId).Select(g => g.Id).ToList();
                if (remaining.Count != groups.Count)
                    await _userRepo.SetGroupsForUserAsync(userId, remaining);
            }

        return (await _departmentRepo.GetDepartmentsForUserAsync(userId)).Select(d => d.Id).ToHashSet();
    }

    /// <summary>
    /// Applies the authorized group diff to the stored memberships: groups the form did not offer
    /// stay as they are (those of departments left before are already gone), and a joined group
    /// must lie within the user's departments (or Global).
    /// </summary>
    private async Task ApplyGroupChangesAsync(
        Guid userId, List<Group> joined, List<Group> left, HashSet<Guid> departmentIds)
    {
        var finalGroupIds = (await _userRepo.GetGroupsForUserAsync(userId))
            .Where(g => left.All(l => l.Id != g.Id))
            .Concat(joined.Where(g => g.DepartmentId == WellKnownGUIDs.DEPARTMENT_GLOBAL
                                      || departmentIds.Contains(g.DepartmentId)))
            .Select(g => g.Id).Distinct().ToList();
        await _userRepo.SetGroupsForUserAsync(userId, finalGroupIds);
    }
}
