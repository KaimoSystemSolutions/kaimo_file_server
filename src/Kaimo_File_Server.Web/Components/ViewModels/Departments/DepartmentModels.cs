using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Security;

namespace Kaimo_File_Server.Web.Components.ViewModels;

// ══════════════════════════════════════════
//  Helper Records
// ══════════════════════════════════════════

public record DeptPermFlag(string Label, string Description, FilePermission Flag);

/// <param name="Department">The department entity.</param>
/// <param name="Depth">Nesting depth (0 = root).</param>
/// <param name="HasChildren">Whether children exist (even if collapsed).</param>
/// <param name="IsLastChild">Last sibling at its level — determines └ vs ├.</param>
/// <param name="AncestorContinues">
///   Length = Depth.  Index i = true means the ancestor at depth i
///   is NOT the last sibling, so a vertical line must be drawn at column i.
/// </param>
public record DepartmentNode(
    Department Department,
    int Depth,
    bool HasChildren,
    bool IsLastChild,
    bool[] AncestorContinues);

public record EffectivePermissionInfo(
    FilePermission Permission,
    bool IsOwn,
    Guid? InheritedFromId,
    string? InheritedFromName)
{
    public bool HasNoDefault => Permission == FilePermission.None
                                && !IsOwn
                                && InheritedFromId == null;
}
