using Kaimo_File_Server.Core.Security;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public class CheckboxItem<T>
{
    public T Item { get; }
    public bool IsChecked { get; set; }
    public CheckboxItem(T item, bool isChecked) { Item = item; IsChecked = isChecked; }
}

/// <param name="InheritedFrom">Set when the assignment is defined on an ancestor department or
/// on Global and only applies to the shown department by inheritance (read-only there).</param>
public record ScopedAssignmentDisplayItem(
    Guid AssignmentId,
    string PrincipalName,
    bool IsGroup,
    string RoleName,
    ScopeType ScopeType,
    string ScopeName,
    string? InheritedFrom = null);

/// <param name="Icon">SVG asset shown in the area's header in the role editor.</param>
public record PermissionGroup(string Label, List<PermissionFlag> Flags, string Icon = "shield.svg");
public record PermissionFlag(ManagementPermission Flag, string Label);
public record PermissionPreset(string Label, ManagementPermission Permissions);