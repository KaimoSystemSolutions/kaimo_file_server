using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Security;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>
/// Authorization of the department editor's assignment diff. Editing a department only grants
/// authority over the department itself; whatever is moved INTO it (users, groups, shares) must
/// already be under the actor's control, otherwise a department admin could pull a global admin,
/// a foreign group or any share into their scope. Unauthorized changes reject the whole save.
/// </summary>
public partial class DepartmentViewModel
{
    private static string ChangesNotPermittedFormat
        => Resources.ResourceManager.GetString("Web_Dept_ChangesNotPermitted")
           ?? "You are not allowed to make the following changes: {0}";

    /// <summary>System groups and the home-folder share always stay in the Global department;
    /// they may only be released back to Global if they ended up elsewhere.</summary>
    private static bool IsPinnedToGlobal(Group group) => WellKnownGUIDs.PROTECTED_GROUPS.Contains(group.Id);

    private static bool IsPinnedToGlobal(ShareDefinition share) => share.IsUserHomes;

    /// <summary>Moving a group or share between departments edits the assignments of BOTH
    /// departments, so the actor needs <see cref="ManagementPermission.EditDepartment"/> on the
    /// entity's current department as well (the selected one is checked by <c>SaveAsync</c>).
    /// Global entities therefore move only with global authority.</summary>
    private Task<bool> CanMoveFromAsync(Guid currentDepartmentId)
        => _mgmtAuth.CanManageDepartmentAsync(_actorContext!, currentDepartmentId, ManagementPermission.EditDepartment);

    private Task<bool> CanMoveGroupAsync(Group group) => CanMoveFromAsync(group.DepartmentId);

    private Task<bool> CanMoveShareAsync(ShareDefinition share) => CanMoveFromAsync(share.DepartmentId);

    /// <summary>Leaving the department also drops the user's memberships in its groups (revoking the
    /// roles they grant), so the actor must outrank the user and be allowed to remove members from
    /// each of those groups.</summary>
    private async Task<bool> CanRemoveMemberAsync(Guid userId, Guid departmentId)
        => await _mgmtAuth.CanManageUserAsync(_actorContext!, userId, ManagementPermission.AssignDepartments)
           && await _roleAssignments.CanLeaveDepartmentGroupsAsync(_actorContext!, userId, departmentId);

    /// <summary>Current members plus every user the actor may add to the department.</summary>
    private async Task<List<CheckboxItem<User>>> BuildMemberPickerAsync(Guid departmentId, IEnumerable<User> allUsers)
    {
        using var evaluation = _mgmtAuth.BeginEvaluationScope(); // read-only batch of checks
        var memberIds = Members.Select(u => u.Id).ToHashSet();
        // Only users within the actor's reach can pass; resolved once, so the full check (incl.
        // the rank) runs for those candidates only. null = global reach.
        var candidateIds = await _mgmtAuth.GetUserIdsInScopeAsync(_actorContext!, ManagementPermission.AssignDepartments);
        var items = new List<CheckboxItem<User>>();
        foreach (var user in allUsers)
        {
            var isMember = memberIds.Contains(user.Id);
            if (isMember
                || ((candidateIds is null || candidateIds.Contains(user.Id))
                    && await _mgmtAuth.CanAddUserToDepartmentAsync(_actorContext!, user.Id, departmentId)))
                items.Add(new CheckboxItem<User>(user, isMember));
        }
        return items;
    }

    /// <summary>The department's own groups plus every group the actor may move into it.</summary>
    private async Task<List<CheckboxItem<Group>>> BuildGroupPickerAsync(Guid departmentId, IEnumerable<Group> allGroups)
    {
        using var evaluation = _mgmtAuth.BeginEvaluationScope(); // read-only batch of checks
        var items = new List<CheckboxItem<Group>>();
        foreach (var group in allGroups)
        {
            var belongs = group.DepartmentId == departmentId;
            if (belongs || (!IsPinnedToGlobal(group) && await CanMoveGroupAsync(group)))
                items.Add(new CheckboxItem<Group>(group, belongs));
        }
        return items;
    }

    /// <summary>The department's own shares plus every share the actor may move into it.</summary>
    private async Task<List<CheckboxItem<ShareDefinition>>> BuildSharePickerAsync(
        Guid departmentId, IEnumerable<ShareDefinition> allShares)
    {
        using var evaluation = _mgmtAuth.BeginEvaluationScope(); // read-only batch of checks
        var items = new List<CheckboxItem<ShareDefinition>>();
        foreach (var share in allShares)
        {
            var belongs = share.DepartmentId == departmentId;
            if (belongs || (!IsPinnedToGlobal(share) && await CanMoveShareAsync(share)))
                items.Add(new CheckboxItem<ShareDefinition>(share, belongs));
        }
        return items;
    }

    /// <summary>
    /// The groups whose checkbox differs from their stored department, re-read from the store so
    /// the decision never rests on the (possibly stale) picker objects. Pinned groups are never
    /// moved in; releasing one that ended up here back to Global stays possible.
    /// </summary>
    private async Task<List<(Group Group, bool ShouldBelong)>> ResolveMovedGroupsAsync(Guid departmentId)
    {
        var moved = new List<(Group, bool)>();
        foreach (var item in EditGroups.DistinctBy(i => i.Item.Id))
        {
            var group = await _groupRepo.GetByIdAsync(item.Item.Id);
            if (group is null || (group.DepartmentId == departmentId) == item.IsChecked) continue;
            if (item.IsChecked && IsPinnedToGlobal(group)) continue;
            moved.Add((group, item.IsChecked));
        }
        return moved;
    }

    /// <summary>Share counterpart of <see cref="ResolveMovedGroupsAsync"/>.</summary>
    private async Task<List<(ShareDefinition Share, bool ShouldBelong)>> ResolveMovedSharesAsync(Guid departmentId)
    {
        var moved = new List<(ShareDefinition, bool)>();
        foreach (var item in EditShares.DistinctBy(i => i.Item.Id))
        {
            var share = await _shareRepo.GetByIdAsync(item.Item.Id);
            if (share is null || (share.DepartmentId == departmentId) == item.IsChecked) continue;
            if (item.IsChecked && IsPinnedToGlobal(share)) continue;
            moved.Add((share, item.IsChecked));
        }
        return moved;
    }

    /// <summary>
    /// Names of the pending changes the actor may not make: adding a user it has no authority
    /// over, removing members without <see cref="ManagementPermission.AssignDepartments"/> or
    /// <see cref="CanRemoveMemberAsync"/>, or moving a group/share it does not control. Empty when
    /// the whole diff is permitted.
    /// </summary>
    private async Task<List<string>> FindRefusedChangesAsync(
        Guid departmentId,
        IReadOnlyCollection<User> addedUsers, IReadOnlyCollection<Guid> removedUserIds,
        IEnumerable<Group> movedGroups, IEnumerable<ShareDefinition> movedShares)
    {
        var refused = new List<string>();

        foreach (var user in addedUsers)
            if (!await _mgmtAuth.CanAddUserToDepartmentAsync(_actorContext!, user.Id, departmentId))
                refused.Add(user.Name);

        if (removedUserIds.Count > 0)
        {
            var mayRemove = await _mgmtAuth.CanManageDepartmentAsync(
                _actorContext!, departmentId, ManagementPermission.AssignDepartments);
            foreach (var member in Members.Where(m => removedUserIds.Contains(m.Id)))
                if (!mayRemove || !await CanRemoveMemberAsync(member.Id, departmentId))
                    refused.Add(member.Name);
        }

        foreach (var group in movedGroups)
            if (!await CanMoveGroupAsync(group))
                refused.Add(group.Name);

        foreach (var share in movedShares)
            if (!await CanMoveShareAsync(share))
                refused.Add(share.Name);

        return refused;
    }
}
