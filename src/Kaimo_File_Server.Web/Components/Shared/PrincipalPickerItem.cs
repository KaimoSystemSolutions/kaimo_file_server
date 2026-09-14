namespace Kaimo_File_Server.Web.Components.Shared;

/// <summary>Selection cardinality for <see cref="PrincipalPickerField"/>.</summary>
public enum SelectionMode
{
    /// <summary>At most one item may be selected; picking a row closes the dialog.</summary>
    Single,
    /// <summary>Any number of items may be selected via checkboxes.</summary>
    Multiple,
}

/// <summary>The kind of principal a picker row represents. Drives the tab, icon and colour.</summary>
public enum PrincipalKind
{
    User,
    Group,
    Role,
    Department,
    Share,
}

/// <summary>
/// A single selectable entry in <see cref="PrincipalPickerField"/>. Callers project their
/// existing entities (users, groups, roles, departments, shares) into this shape; the picker
/// stays pure presentation and needs no repository access.
/// </summary>
/// <param name="Id">Stable identifier used for selection tracking.</param>
/// <param name="Name">Display name (also the search and sort key).</param>
/// <param name="Kind">Principal type, used for tabs, icon and colour.</param>
/// <param name="Hint">Optional secondary line (e.g. username or member count).</param>
/// <param name="Color">Optional custom <c>#RRGGBB</c> color (departments); null uses the kind default / automatic color.</param>
/// <param name="GroupLabel">Optional bucket label (e.g. a group's department). When present, the picker
/// lists rows of this kind under colored section headers instead of one flat list.</param>
/// <param name="GroupColor">Resolved stroke/text color for the bucket; also colors the row and chip.</param>
/// <param name="GroupSoft">Resolved soft background matching <paramref name="GroupColor"/>.</param>
public record PrincipalPickerItem(
    Guid Id, string Name, PrincipalKind Kind, string? Hint = null, string? Color = null,
    string? GroupLabel = null, string? GroupColor = null, string? GroupSoft = null);
