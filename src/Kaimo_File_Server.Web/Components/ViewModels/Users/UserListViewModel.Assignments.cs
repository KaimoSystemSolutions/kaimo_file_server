using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Language;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public partial class UserListViewModel
{
    // -- Scoped Assignment Creation --
    public bool IsAddingAssignment { get; set; }
    public ScopeType NewAssignmentScopeType { get; set; } = ScopeType.Department;
    public Guid? NewAssignmentScopeId { get; set; }
    public Guid? NewAssignmentPrincipalId { get; set; }
    public bool NewAssignmentPrincipalIsGroup { get; set; }

    /// <summary>The role to assign. Preset to the selected role on the role detail; chosen
    /// in the form when the assignment is started from a user profile.</summary>
    public Guid? NewAssignmentRoleId { get; set; }
    public List<Role> AllRoles { get; private set; } = [];
    public List<User> AllUsers { get; private set; } = [];
    public List<Group> AllGroups { get; private set; } = [];

    /// <summary>All shares, offered in the scope selector when assigning a role at Share scope.
    /// A share's GUID is not something an operator knows by heart, so the picker lists shares
    /// by name rather than asking for a raw identifier.</summary>
    public List<ShareDefinition> AllShares { get; private set; } = [];

    // ══════════════════════════════════════════
    //  Scoped Role Assignments
    // ══════════════════════════════════════════

    public void StartAddAssignment()
    {
        IsAddingAssignment = true;
        NewAssignmentScopeType = ScopeType.Department;
        NewAssignmentScopeId = null;
        NewAssignmentPrincipalId = null;
        NewAssignmentPrincipalIsGroup = false;
        NewAssignmentRoleId = SelectedRole?.Id;
        ErrorMessage = null;
    }

    public void CancelAddAssignment()
    {
        IsAddingAssignment = false;
        ErrorMessage = null;
    }

    public async Task LoadAssignmentFormDataAsync()
    {
        AllRoles = (await _roleRepo.GetAllAsync()).OrderBy(r => r.Name).ToList();
        // Only recipients within the actor's reach are offered (enforced again on create).
        var (users, groups) = _actorContext is null
            ? (new List<User>(), new List<Group>())
            : await _roleAssignments.GetAssignableRecipientsAsync(_actorContext);
        AllUsers = users.OrderBy(u => u.Name).ToList();
        AllGroups = groups.OrderBy(g => g.Name).ToList();
        AllShares = (await _shareRepo.GetAllAsync()).OrderBy(s => s.Name).ToList();
    }

    public async Task CreateAssignmentAsync()
    {
        if (_actorContext is null) return;

        var role = NewAssignmentRoleId is { } roleId ? await _roleRepo.GetByIdAsync(roleId) : null;
        if (role is null)
        { ErrorMessage = Resources.Web_Error_SelectRole; return; }
        if (NewAssignmentPrincipalId is null)
        { ErrorMessage = Resources.Web_Error_SelectUserOrGroup; return; }
        if (NewAssignmentScopeType != ScopeType.Global && NewAssignmentScopeId is null)
        { ErrorMessage = Resources.Web_Scope_SelectScope; return; }

        var scopeId = NewAssignmentScopeType == ScopeType.Global
            ? Guid.Empty : NewAssignmentScopeId!.Value;

        try
        {
            IsSaving = true;
            ErrorMessage = null;

            // The shared service applies the no-privilege-elevation gate, maps the Global
            // department onto the system-wide scope and keeps the Admins group in sync.
            var error = await _roleAssignments.CreateAsync(
                _actorContext, NewAssignmentPrincipalId.Value, role, NewAssignmentScopeType, scopeId);
            if (error is not null) { ErrorMessage = error; return; }

            IsAddingAssignment = false;
            if (SelectedRole is not null) await LoadRoleScopedAssignmentsAsync(SelectedRole.Id);
            if (SelectedUser is not null) await LoadUserScopedAssignmentsAsync(SelectedUser.Id);
            SuccessMessage = Resources.Web_Assignment_Created;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating the assignment");
            ErrorMessage = Resources.Web_Error_CreateAssignmentFailed;
        }
        finally { IsSaving = false; }
    }

    public async Task DeleteAssignmentAsync(Guid assignmentId)
    {
        if (_actorContext is null) return;

        var assignment = await _assignmentRepo.GetByIdAsync(assignmentId);
        if (assignment is null) { ErrorMessage = Resources.Web_Error_NoPermission; return; }

        try
        {
            IsSaving = true;
            ErrorMessage = null;

            // Elevation gate, last-admin guard and the Admins-group mirror live in the shared service.
            var error = await _roleAssignments.DeleteAsync(_actorContext, assignment);
            if (error is not null) { ErrorMessage = error; return; }

            if (SelectedRole is not null) await LoadRoleScopedAssignmentsAsync(SelectedRole.Id);
            if (SelectedUser is not null) await LoadUserScopedAssignmentsAsync(SelectedUser.Id);
            SuccessMessage = Resources.Web_Assignment_Removed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting assignment {Id}", assignmentId);
            ErrorMessage = Resources.Web_Error_DeleteAssignmentFailed;
        }
        finally { IsSaving = false; }
    }

    private async Task LoadRoleScopedAssignmentsAsync(Guid roleId)
    {
        var allAssignments = await _assignmentRepo.GetByRoleAsync(roleId);
        var users = await _userRepo.GetAllAsync();
        var groups = await _groupRepo.GetAllAsync();
        RoleScopedAssignments = await ResolveScopedAssignmentsAsync(allAssignments, users, groups);
    }

    private async Task LoadUserScopedAssignmentsAsync(Guid userId)
    {
        var assignments = await _assignmentRepo.GetByPrincipalAsync(userId);
        var users = await _userRepo.GetAllAsync();
        var groups = await _groupRepo.GetAllAsync();
        UserScopedAssignments = await ResolveScopedAssignmentsAsync(assignments, users, groups);
    }

    private async Task<List<ScopedAssignmentDisplayItem>> ResolveScopedAssignmentsAsync(
        List<ScopedRoleAssignment> assignments,
        IEnumerable<User> users,
        IEnumerable<Group> groups)
    {
        var userLookup = users.ToDictionary(u => u.Id);
        var groupLookup = groups.ToDictionary(g => g.Id);
        var deptLookup = AllDepartments.ToDictionary(d => d.Id);
        var roles = await _roleRepo.GetAllAsync();
        var roleLookup = roles.ToDictionary(r => r.Id);
        var shareLookup = (await _shareRepo.GetAllAsync()).ToDictionary(s => s.Id);

        var items = new List<ScopedAssignmentDisplayItem>();

        foreach (var a in assignments)
        {
            var principalName = userLookup.TryGetValue(a.PrincipalId, out var user)
                ? user.Name
                : groupLookup.TryGetValue(a.PrincipalId, out var group)
                    ? group.Name : a.PrincipalId.ToString();

            var isGroup = groupLookup.ContainsKey(a.PrincipalId);

            var roleName = roleLookup.TryGetValue(a.RoleId, out var role)
                ? role.Name : a.RoleId.ToString();

            var scopeName = a.ScopeType switch
            {
                ScopeType.Global => "Global",
                ScopeType.Department => deptLookup.TryGetValue(a.ScopeId, out var dept)
                    ? dept.Name : a.ScopeId.ToString(),
                ScopeType.Share => shareLookup.TryGetValue(a.ScopeId, out var share)
                    ? share.Name : a.ScopeId.ToString(),
                _ => a.ScopeId.ToString()
            };

            items.Add(new ScopedAssignmentDisplayItem(
                a.Id, principalName, isGroup, roleName, a.ScopeType, scopeName));
        }

        // System-wide assignments carry the most weight, so they lead the list.
        return items.OrderBy(i => i.ScopeType != ScopeType.Global)
            .ThenBy(i => i.PrincipalName).ThenBy(i => i.RoleName).ToList();
    }

    // ══════════════════════════════════════════
    //  Last-Admin Protection
    // ══════════════════════════════════════════

    /// <summary>A role that carries the complete <see cref="ManagementPermission.FullAdmin"/>
    /// mask — the same definition the page uses for <c>IsGlobalAdmin</c>.</summary>
    private static bool IsAdminRole(Role role) => RoleAssignmentService.IsAdminRole(role);

    /// <summary>
    /// The set of ENABLED users who effectively hold a global FullAdmin role, either
    /// directly or inherited through group membership. No operation may reduce it to empty,
    /// or the whole management UI becomes permanently unreachable.
    /// </summary>
    private Task<HashSet<Guid>> GetEnabledGlobalAdminUserIdsAsync(Guid? excludeAssignmentId = null)
        => _roleAssignments.GetEnabledGlobalAdminUserIdsAsync(excludeAssignmentId);

    /// <summary>Keeps the admin role and the Admins group of a user in lockstep (full mirror).
    /// Callers are responsible for the last-admin guard before revoking.</summary>
    private Task ReconcileAdminCouplingAsync(Guid userId, bool shouldBeAdmin)
        => _roleAssignments.ReconcileAdminCouplingAsync(userId, shouldBeAdmin);

    /// <summary>Whether <paramref name="userId"/> inherits a global FullAdmin role through
    /// any group it belongs to. Used to tell whether unchecking the user's OWN admin role
    /// still leaves it an admin (group-inherited authority is untouched by that edit).</summary>
    private async Task<bool> UserInheritsGlobalAdminFromGroupAsync(Guid userId, Guid? ignoreGroupId = null)
    {
        var globalAssignments = await _assignmentRepo.GetByScopeAsync(ScopeType.Global, Guid.Empty);
        var roles = (await _roleRepo.GetAllAsync()).ToDictionary(r => r.Id);
        var groupIds = (await _groupRepo.GetAllAsync()).Select(g => g.Id).ToHashSet();

        foreach (var a in globalAssignments)
        {
            if (!groupIds.Contains(a.PrincipalId) || a.PrincipalId == ignoreGroupId) continue;
            if (!roles.TryGetValue(a.RoleId, out var role) || !IsAdminRole(role)) continue;
            if ((await _groupRepo.GetMembersAsync(a.PrincipalId)).Any(m => m.Id == userId))
                return true;
        }
        return false;
    }
}
