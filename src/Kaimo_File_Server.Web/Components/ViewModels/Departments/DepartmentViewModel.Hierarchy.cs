using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Security;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>
/// Authorization of the department hierarchy. Placing a department below a parent extends the
/// parent's admin scope over it, and taking it away from a parent removes that oversight, so a
/// parent change needs <see cref="ManagementPermission.EditDepartment"/> on the old AND the new
/// parent; "top level" (directly below Global) counts as the Global department and therefore
/// needs global authority. Creating a department is placing it below its parent.
/// </summary>
public partial class DepartmentViewModel
{
    private static string InvalidParentMessage
        => Resources.ResourceManager.GetString("Web_Dept_InvalidParent")
           ?? "The selected parent department is not valid.";

    /// <summary>Parents offered when creating a department: those the actor may place below.</summary>
    public List<Department> CreateParentOptions { get; private set; } = [];

    /// <summary>Whether the actor may create a top-level department (global authority).</summary>
    public bool CanCreateTopLevel { get; private set; }

    /// <summary>Whether the edited department may be moved to the top level (picker can be cleared).</summary>
    public bool CanMoveToTopLevel { get; private set; }

    /// <summary>
    /// Deleting a department takes it away from its parent, so it needs
    /// <see cref="ManagementPermission.EditDepartment"/> on the parent (top level: global authority).
    /// An admin can therefore delete departments below their own, never the one their assignment
    /// sits on. The Global department is never deleted.
    /// </summary>
    private async Task<bool> CanDeleteDepartmentAsync(Department department)
        => _actorContext is not null && !IsGlobalDepartment(department)
           && await CanPlaceUnderAsync(department.ParentDepartmentId);

    private Task<bool> CanPlaceUnderAsync(Guid? parentId)
        => _mgmtAuth.CanManageDepartmentAsync(_actorContext!,
            parentId ?? WellKnownGUIDs.DEPARTMENT_GLOBAL, ManagementPermission.EditDepartment);

    /// <summary>
    /// The departments the actor may place below, resolved once for a whole picker — same rule as
    /// <see cref="CanPlaceUnderAsync"/> for an existing department.
    /// </summary>
    private async Task<Func<Guid, bool>> LoadPlaceableParentFilterAsync()
    {
        var scope = await _mgmtAuth.GetAuthorizedDepartmentIdsAsync(_actorContext!, ManagementPermission.EditDepartment)
                    ?? AuthorizedScopeResult.None();
        if (scope.IsUnrestricted)
            return _ => true;
        var ids = scope.ScopeIds.ToHashSet();
        return ids.Contains;
    }

    private async Task LoadCreateParentOptionsAsync()
    {
        using var evaluation = _mgmtAuth.BeginEvaluationScope(); // read-only batch of checks
        CanCreateTopLevel = await CanPlaceUnderAsync(null);
        var canPlaceUnder = await LoadPlaceableParentFilterAsync();
        CreateParentOptions = Departments.Where(d => !IsGlobalDepartment(d) && canPlaceUnder(d.Id)).ToList();
    }

    /// <summary>
    /// Parents offered when editing <paramref name="department"/>: none but the current one when
    /// the actor may not take it away from that parent; otherwise every authorized parent that is
    /// neither the department itself nor one of its descendants.
    /// </summary>
    private async Task<List<Department>> LoadAvailableParentsAsync(Department department)
    {
        using var evaluation = _mgmtAuth.BeginEvaluationScope(); // read-only batch of checks
        var all = await _departmentRepo.GetAllAsync();
        var current = all.Where(d => d.Id == department.ParentDepartmentId).ToList();
        CanMoveToTopLevel = false;
        if (!await CanPlaceUnderAsync(department.ParentDepartmentId))
            return current;

        CanMoveToTopLevel = await CanPlaceUnderAsync(null);
        var excluded = await _departmentRepo.GetDescendantIdsAsync(department.Id);
        excluded.Add(department.Id);
        excluded.Add(WellKnownGUIDs.DEPARTMENT_GLOBAL);

        var canPlaceUnder = await LoadPlaceableParentFilterAsync();
        return all
            .Where(d => !excluded.Contains(d.Id) && (d.Id == department.ParentDepartmentId || canPlaceUnder(d.Id)))
            .OrderByGlobalFirst().ToList();
    }

    /// <summary>
    /// Server-side validation of a parent change: the new parent must exist and must not be the
    /// department or one of its descendants (no cycles), and the actor needs authority over both
    /// parents. Returns a localized error or <c>null</c>.
    /// </summary>
    private async Task<string?> ValidateParentChangeAsync(Guid departmentId, Guid? oldParentId, Guid? newParentId)
    {
        if (newParentId is { } parentId
            && (parentId == departmentId
                || await _departmentRepo.GetByIdAsync(parentId) is null
                || (await _departmentRepo.GetDescendantIdsAsync(departmentId)).Contains(parentId)))
            return InvalidParentMessage;

        return await CanPlaceUnderAsync(oldParentId) && await CanPlaceUnderAsync(newParentId)
            ? null
            : Resources.Web_Dept_NoPermissionInDept;
    }

    /// <summary>Validation of the parent of a new department. Returns a localized error or <c>null</c>.</summary>
    private async Task<string?> ValidateNewParentAsync(Guid? parentId)
    {
        if (parentId is { } id && await _departmentRepo.GetByIdAsync(id) is null)
            return InvalidParentMessage;

        return await CanPlaceUnderAsync(parentId) ? null : Resources.Web_Dept_NoPermissionInDept;
    }
}
