using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Helpers;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public partial class DepartmentViewModel
{
    // ══════════════════════════════════════════
    //  Tree helpers
    // ══════════════════════════════════════════

    private readonly HashSet<Guid> _collapsedIds = [];

    public bool IsCollapsed(Guid id) => _collapsedIds.Contains(id);

    public void ToggleCollapse(Guid id)
    {
        if (!_collapsedIds.Remove(id))
            _collapsedIds.Add(id);
    }

    public void CollapseAll()
    {
        foreach (var d in Departments.Where(d => GetChildCount(d) > 0))
            _collapsedIds.Add(d.Id);
    }

    /// <summary>
    /// The parent a department is shown under: its own parent when that is visible, otherwise
    /// the Global department, which is drawn as the root of every top-level department.
    /// </summary>
    private Guid? TreeParentId(Department dept)
    {
        if (IsGlobalDepartment(dept)) return null;
        if (dept.ParentDepartmentId is { } parentId && Departments.Any(d => d.Id == parentId))
            return parentId;
        return Departments.Any(IsGlobalDepartment) ? WellKnownGUIDs.DEPARTMENT_GLOBAL : null;
    }

    /// <summary>Number of departments shown directly below <paramref name="dept"/> in the tree.</summary>
    public int GetChildCount(Department dept) => Departments.Count(d => TreeParentId(d) == dept.Id);

    public void ExpandAll() => _collapsedIds.Clear();

    public List<DepartmentNode> GetTreeOrderedDepartments()
    {
        var result = new List<DepartmentNode>();

        var roots = Departments
            .Where(d => TreeParentId(d) is null)
            .OrderBy(d => IsGlobalDepartment(d) ? 0 : 1)
            .ThenBy(d => d.Name)
            .ToList();

        for (var i = 0; i < roots.Count; i++)
            AppendTree(result, roots[i], 0, i == roots.Count - 1, []);

        return result;
    }

    private void AppendTree(
        List<DepartmentNode> result,
        Department dept,
        int depth,
        bool isLast,
        bool[] ancestorContinues)
    {
        var children = Departments
            .Where(d => TreeParentId(d) == dept.Id)
            .OrderBy(d => d.Name)
            .ToList();

        result.Add(new DepartmentNode(dept, depth, children.Count > 0, isLast, ancestorContinues));

        if (_collapsedIds.Contains(dept.Id))
            return;

        for (var i = 0; i < children.Count; i++)
        {
            var childContinues = new bool[depth + 1];
            Array.Copy(ancestorContinues, childContinues, depth);
            childContinues[depth] = i < children.Count - 1;

            AppendTree(result, children[i], depth + 1, i == children.Count - 1, childContinues);
        }
    }
}
