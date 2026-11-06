namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>
/// Removing members drops their memberships in the department's groups and their roles at the
/// department. <see cref="SaveAsync"/> stops before writing and lists those losses in
/// <see cref="PendingDepartmentLeaves"/>; the save only runs after
/// <see cref="ConfirmDepartmentLeaveAsync"/>.
/// </summary>
public partial class DepartmentViewModel
{
    private bool _departmentLeaveConfirmed;

    /// <summary>Non-empty while the warning about the members' lost groups and roles is shown.</summary>
    public IReadOnlyList<DepartmentLeaveWarning> PendingDepartmentLeaves { get; private set; } = [];

    /// <summary>Runs the save the warning interrupted; it is authorized again from scratch.</summary>
    public async Task ConfirmDepartmentLeaveAsync()
    {
        if (PendingDepartmentLeaves.Count == 0) return;
        PendingDepartmentLeaves = [];
        _departmentLeaveConfirmed = true;
        try { await SaveAsync(); }
        finally { _departmentLeaveConfirmed = false; }
    }

    public void CancelDepartmentLeave() => PendingDepartmentLeaves = [];

    /// <summary>The removed members that lose groups or roles, unless the leave was confirmed.</summary>
    private async Task<List<DepartmentLeaveWarning>> CollectLeaveWarningsAsync(
        Guid departmentId, IReadOnlyCollection<Guid> removedUserIds)
    {
        var warnings = new List<DepartmentLeaveWarning>();
        if (_departmentLeaveConfirmed) return warnings;

        foreach (var member in Members.Where(m => removedUserIds.Contains(m.Id)))
        {
            var impact = await _roleAssignments.GetDepartmentLeaveImpactAsync(member.Id, departmentId);
            if (!impact.IsEmpty)
                warnings.Add(DepartmentLeaveWarning.From(member.Name, impact));
        }
        return warnings;
    }
}
