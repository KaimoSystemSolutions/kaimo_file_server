namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>
/// Unchecking a department in the user form drops the user's memberships in its groups and the
/// user's roles there. <see cref="SaveUserAsync"/> stops before writing and lists those losses in
/// <see cref="PendingDepartmentLeaves"/>; the save only runs after
/// <see cref="ConfirmDepartmentLeaveAsync"/>.
/// </summary>
public partial class UserListViewModel
{
    private bool _departmentLeaveConfirmed;

    /// <summary>Non-empty while the warning about the user's lost groups and roles is shown.</summary>
    public IReadOnlyList<DepartmentLeaveWarning> PendingDepartmentLeaves { get; private set; } = [];

    /// <summary>Runs the save the warning interrupted; it is authorized again from scratch.</summary>
    public async Task ConfirmDepartmentLeaveAsync()
    {
        if (PendingDepartmentLeaves.Count == 0) return;
        PendingDepartmentLeaves = [];
        _departmentLeaveConfirmed = true;
        try { await SaveUserAsync(); }
        finally { _departmentLeaveConfirmed = false; }
    }

    public void CancelDepartmentLeave() => PendingDepartmentLeaves = [];

    /// <summary>The unchecked departments in which the user loses groups or roles, unless the
    /// leave was confirmed.</summary>
    private async Task<List<DepartmentLeaveWarning>> CollectLeaveWarningsAsync(Guid userId)
    {
        var warnings = new List<DepartmentLeaveWarning>();
        if (_departmentLeaveConfirmed || !CanAssignDepartments) return warnings;

        foreach (var department in PendingDepartmentLeaveDepartments())
        {
            var impact = await _roleAssignments.GetDepartmentLeaveImpactAsync(userId, department.Id);
            if (!impact.IsEmpty)
                warnings.Add(DepartmentLeaveWarning.From(department.Name, impact));
        }
        return warnings;
    }
}
