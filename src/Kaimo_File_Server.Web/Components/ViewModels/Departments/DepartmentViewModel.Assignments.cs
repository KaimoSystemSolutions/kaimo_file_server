using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Language;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public partial class DepartmentViewModel
{
    // -- Add department-scoped role assignment --
    public bool IsAddingAssignment { get; private set; }
    public Guid? NewAssignmentRoleId { get; set; }
    public Guid? NewAssignmentPrincipalId { get; set; }
    public List<Role> AllRoles { get; private set; } = [];
    public List<User> AllUsers { get; private set; } = [];
    public List<Group> AllGroups { get; private set; } = [];

    // ══════════════════════════════════════════
    //  Department-scoped role assignments
    // ══════════════════════════════════════════

    private async Task LoadRoleAssignmentsAsync(Guid departmentId)
    {
        var users = (await _userRepo.GetAllAsync()).ToDictionary(u => u.Id, u => u.Name);
        var groups = (await _groupRepo.GetAllAsync()).ToDictionary(g => g.Id, g => g.Name);
        var roles = (await _roleRepo.GetAllAsync()).ToDictionary(r => r.Id, r => r.Name);
        var globalName = (await _departmentRepo.GetByIdAsync(WellKnownGUIDs.DEPARTMENT_GLOBAL))?.Name ?? "Global";

        ScopedAssignmentDisplayItem ToItem(ScopedRoleAssignment a, string scopeName, string? inheritedFrom) => new(
            a.Id,
            users.GetValueOrDefault(a.PrincipalId) ?? groups.GetValueOrDefault(a.PrincipalId) ?? a.PrincipalId.ToString(),
            groups.ContainsKey(a.PrincipalId),
            roles.GetValueOrDefault(a.RoleId) ?? a.RoleId.ToString(),
            a.ScopeType,
            scopeName,
            inheritedFrom);

        var items = new List<ScopedAssignmentDisplayItem>();
        if (departmentId == WellKnownGUIDs.DEPARTMENT_GLOBAL)
        {
            // The Global department is the system-wide scope.
            items.AddRange((await _assignmentRepo.GetByScopeAsync(ScopeType.Global, Guid.Empty))
                .Select(a => ToItem(a, globalName, null)));
        }
        else
        {
            var deptName = (await _departmentRepo.GetByIdAsync(departmentId))?.Name ?? departmentId.ToString();
            items.AddRange((await _assignmentRepo.GetByScopeAsync(ScopeType.Department, departmentId))
                .Select(a => ToItem(a, deptName, null)));

            // Department scope covers descendants, and Global covers everything, so the
            // assignments of every ancestor and of Global apply here too.
            foreach (var ancestor in await _departmentRepo.GetAncestorChainAsync(departmentId))
                items.AddRange((await _assignmentRepo.GetByScopeAsync(ScopeType.Department, ancestor.Id))
                    .Select(a => ToItem(a, ancestor.Name, ancestor.Name)));
            items.AddRange((await _assignmentRepo.GetByScopeAsync(ScopeType.Global, Guid.Empty))
                .Select(a => ToItem(a, globalName, globalName)));
        }

        // System-wide assignments lead the list, then the department's own, then inherited ones.
        RoleAssignments = items
            .OrderBy(i => i.ScopeType != ScopeType.Global).ThenBy(i => i.InheritedFrom is not null).ThenBy(i => i.RoleName).ThenBy(i => i.PrincipalName)
            .ToList();
    }

    /// <summary>Whether <paramref name="assignment"/> is one of the selected department's own
    /// (editable) assignments; on the Global department those are the system-wide ones.</summary>
    private bool IsOwnAssignment(ScopedRoleAssignment assignment)
        => Selected is not null && (Selected.Id == WellKnownGUIDs.DEPARTMENT_GLOBAL
            ? assignment.ScopeType == ScopeType.Global
            : assignment.ScopeType == ScopeType.Department && assignment.ScopeId == Selected.Id);

    public async Task StartAddAssignmentAsync()
    {
        IsAddingAssignment = true;
        NewAssignmentRoleId = null;
        NewAssignmentPrincipalId = null;
        ErrorMessage = null;
        AllRoles = (await _roleRepo.GetAllAsync()).OrderBy(r => r.Name).ToList();
        AllUsers = (await _userRepo.GetAllAsync()).OrderBy(u => u.Name).ToList();
        AllGroups = (await _groupRepo.GetAllAsync()).OrderBy(g => g.Name).ToList();
    }

    public void CancelAddAssignment()
    {
        IsAddingAssignment = false;
        ErrorMessage = null;
    }

    /// <summary>Assigns the chosen role to the chosen user or group, scoped to the selected
    /// department. On the Global department this is a system-wide assignment, so the shared
    /// service also applies the Admins-group mirror.</summary>
    public async Task CreateAssignmentAsync()
    {
        if (Selected is null || _actorContext is null) return;

        var role = NewAssignmentRoleId is { } roleId ? await _roleRepo.GetByIdAsync(roleId) : null;
        if (role is null)
        { ErrorMessage = Resources.Web_Error_SelectRole; return; }
        if (NewAssignmentPrincipalId is not { } principalId)
        { ErrorMessage = Resources.Web_Error_SelectUserOrGroup; return; }

        try
        {
            IsSaving = true;
            ErrorMessage = null;
            var error = await _roleAssignments.CreateAsync(
                _actorContext, principalId, role, ScopeType.Department, Selected.Id);
            if (error is not null) { ErrorMessage = error; return; }

            IsAddingAssignment = false;
            await LoadRoleAssignmentsAsync(Selected.Id);
            SuccessMessage = Resources.Web_Assignment_Created;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating the department role assignment");
            ErrorMessage = Resources.Web_Error_CreateAssignmentFailed;
        }
        finally { IsSaving = false; }
    }

    /// <summary>Removes one of the selected department's own assignments. Inherited ones are
    /// rejected here (they are removed where they are defined); the last-admin guard applies
    /// through the shared service.</summary>
    public async Task DeleteAssignmentAsync(Guid assignmentId)
    {
        if (Selected is null || _actorContext is null) return;

        var assignment = await _assignmentRepo.GetByIdAsync(assignmentId);
        if (assignment is null || !IsOwnAssignment(assignment))
        { ErrorMessage = Resources.Web_Error_NoPermission; return; }

        try
        {
            IsSaving = true;
            ErrorMessage = null;
            var error = await _roleAssignments.DeleteAsync(_actorContext, assignment);
            if (error is not null) { ErrorMessage = error; return; }

            await LoadRoleAssignmentsAsync(Selected.Id);
            SuccessMessage = Resources.Web_Assignment_Removed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting department role assignment {Id}", assignmentId);
            ErrorMessage = Resources.Web_Error_DeleteAssignmentFailed;
        }
        finally { IsSaving = false; }
    }

    // ══════════════════════════════════════════
    //  Effective Permission Resolution
    //  (replaces IDepartmentPermissionService)
    // ══════════════════════════════════════════

    private async Task<EffectivePermissionInfo> ResolveEffectivePermissionAsync(Guid departmentId)
    {
        var dept = await _departmentRepo.GetByIdAsync(departmentId);
        if (dept == null)
            return new EffectivePermissionInfo(FilePermission.None, false, null, null);

        if (dept.DefaultFilePermission.HasValue)
            return new EffectivePermissionInfo(
                (FilePermission)dept.DefaultFilePermission.Value, true, null, null);

        var ancestors = await _departmentRepo.GetAncestorChainAsync(departmentId);
        foreach (var ancestor in ancestors)
        {
            if (ancestor.DefaultFilePermission.HasValue)
                return new EffectivePermissionInfo(
                    (FilePermission)ancestor.DefaultFilePermission.Value,
                    false, ancestor.Id, ancestor.Name);
        }

        return new EffectivePermissionInfo(FilePermission.None, false, null, null);
    }
}
