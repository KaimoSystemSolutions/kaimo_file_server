using Kaimo_File_Server.Core.Security;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public class CheckboxItem<T>
{
    public T Item { get; }
    public bool IsChecked { get; set; }
    public CheckboxItem(T item, bool isChecked) { Item = item; IsChecked = isChecked; }
}

public record ScopedAssignmentDisplayItem(
    Guid AssignmentId,
    string PrincipalName,
    bool IsGroup,
    string RoleName,
    ScopeType ScopeType,
    string ScopeName);

public record PermissionGroup(string Label, List<PermissionFlag> Flags);
public record PermissionFlag(ManagementPermission Flag, string Label);
public record PermissionPreset(string Label, ManagementPermission Permissions);