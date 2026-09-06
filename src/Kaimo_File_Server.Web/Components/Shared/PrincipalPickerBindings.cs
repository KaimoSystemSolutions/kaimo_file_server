using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Web.Components.ViewModels;

namespace Kaimo_File_Server.Web.Components.Shared;

/// <summary>
/// Glue between the existing <see cref="CheckboxItem{T}"/> lists that back the edit forms
/// and <see cref="PrincipalPickerField"/>. Lets a call site drop the picker in without new
/// code-behind: project the list to picker items, read the checked ids, and write a new
/// selection back onto the same list (so the view models' Save logic is untouched).
/// </summary>
public static class PrincipalPickerBindings
{
    public static IReadOnlyList<PrincipalPickerItem> ToPickerItems<T>(
        this IEnumerable<CheckboxItem<T>> items, PrincipalKind kind,
        Func<T, Guid> id, Func<T, string> name, Func<T, string?>? hint = null)
        => items.Select(c => new PrincipalPickerItem(id(c.Item), name(c.Item), kind, hint?.Invoke(c.Item))).ToList();

    public static IReadOnlyCollection<Guid> CheckedIds<T>(
        this IEnumerable<CheckboxItem<T>> items, Func<T, Guid> id)
        => items.Where(c => c.IsChecked).Select(c => id(c.Item)).ToList();

    public static void ApplySelection<T>(
        this IEnumerable<CheckboxItem<T>> items, IReadOnlyList<Guid> ids, Func<T, Guid> id)
    {
        var set = ids.ToHashSet();
        foreach (var c in items)
            c.IsChecked = set.Contains(id(c.Item));
    }

    // -- Single-select helpers for department pickers --

    public static IReadOnlyList<PrincipalPickerItem> ToDepartmentItems(this IEnumerable<Department> depts) =>
        depts.Select(d => new PrincipalPickerItem(d.Id, d.Name, PrincipalKind.Department)).ToList();

    /// <summary>Wraps an optional id as a single-element selection (empty when null).</summary>
    public static IReadOnlyCollection<Guid> AsSelection(this Guid? id) =>
        id is { } g ? new[] { g } : Array.Empty<Guid>();

    /// <summary>Wraps an id as a single-element selection, treating <see cref="Guid.Empty"/> as no selection.</summary>
    public static IReadOnlyCollection<Guid> AsSelection(this Guid id) =>
        id == Guid.Empty ? Array.Empty<Guid>() : new[] { id };

    /// <summary>The first selected id, or null when the selection is empty.</summary>
    public static Guid? FirstOrNull(this IReadOnlyList<Guid> ids) =>
        ids.Count > 0 ? ids[0] : null;
}
