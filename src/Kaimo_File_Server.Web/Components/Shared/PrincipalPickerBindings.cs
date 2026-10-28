using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
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

    /// <summary>
    /// Projects group checkboxes into picker items bucketed and colored by their department,
    /// so the picker lists them under per-department headers (see <see cref="PrincipalPickerField"/>).
    /// </summary>
    public static IReadOnlyList<PrincipalPickerItem> ToGroupItems(
        this IEnumerable<CheckboxItem<Group>> items, Func<Guid, DepartmentDisplay> department)
        => items.Select(c =>
        {
            var dept = department(c.Item.DepartmentId);
            return new PrincipalPickerItem(
                c.Item.Id, c.Item.Name, PrincipalKind.Group, Hint: dept.Name,
                GroupLabel: dept.Name, GroupColor: dept.Color, GroupSoft: dept.Soft);
        }).ToList();

    // -- Single-select helpers for department pickers --

    public static IReadOnlyList<PrincipalPickerItem> ToDepartmentItems(this IEnumerable<Department> depts) =>
        depts.Select(d => new PrincipalPickerItem(d.Id, d.Name, PrincipalKind.Department, Color: d.Color)).ToList();

    public static IReadOnlyList<PrincipalPickerItem> ToRoleItems(this IEnumerable<Role> roles) =>
        roles.Select(r => new PrincipalPickerItem(r.Id, r.Name, PrincipalKind.Role)).ToList();

    /// <summary>Users and groups as one picker list (role assignees); the picker's own tabs separate them.</summary>
    public static IReadOnlyList<PrincipalPickerItem> ToAssigneeItems(this IEnumerable<User> users, IEnumerable<Group> groups) =>
        users.Select(u => new PrincipalPickerItem(u.Id, u.Name, PrincipalKind.User, u.Username))
            .Concat(groups.Select(g => new PrincipalPickerItem(g.Id, g.Name, PrincipalKind.Group)))
            .ToList();

    // -- Storage pools: identified by their path, so the picker gets a stable id derived from it --

    /// <summary>Stable picker id for a pool path.</summary>
    public static Guid PoolId(string poolPath) =>
        new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(poolPath)));

    /// <summary>Pools as picker items: the given display name, the path as hint.</summary>
    public static IReadOnlyList<PrincipalPickerItem> ToPoolItems<T>(this IEnumerable<T> pools, Func<T, string> path, Func<T, string> name) =>
        pools.Select(p => new PrincipalPickerItem(PoolId(path(p)), name(p), PrincipalKind.Pool, path(p))).ToList();

    /// <summary>The pool path behind the first selected id, or null when nothing (known) is selected.</summary>
    public static string? PoolPathFor(this IEnumerable<string> poolPaths, IReadOnlyList<Guid> ids) =>
        ids.Count == 0 ? null : poolPaths.FirstOrDefault(p => PoolId(p) == ids[0]);

    /// <summary>A pool path as a single-element selection (empty when blank).</summary>
    public static IReadOnlyCollection<Guid> AsPoolSelection(this string? poolPath) =>
        string.IsNullOrEmpty(poolPath) ? Array.Empty<Guid>() : new[] { PoolId(poolPath) };

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
