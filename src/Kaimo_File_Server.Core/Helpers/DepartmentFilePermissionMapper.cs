using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Security;

namespace Kaimo_File_Server.Core.Helpers;

/// <summary>
/// Maps between <see cref="FilePermission"/> (used by the ACL system)
/// and the department default permission bitmask (long flags).
///
/// Department default flags (from DepartmentPermissionDisplay):
///   1L  = Read          → FilePermission.ListReadData
///   2L  = Write         → FilePermission.CreateWriteData
///   4L  = Delete        → FilePermission.Delete
///   8L  = Create files  → FilePermission.CreateWriteData
///   16L = Create folders → FilePermission.CreateWriteData
///   32L = Rename        → FilePermission.Delete + FilePermission.CreateWriteData
///
/// This mapping is intentionally conservative:
///   - "Read" grants listing and reading
///   - "Write" / "Create files" / "Create folders" all grant CreateWriteData
///   - "Delete" grants delete
///   - "Rename" requires both delete (old) and create (new)
/// </summary>
public static class DepartmentFilePermissionMapper
{
    private const long DeptRead = 1L;
    private const long DeptWrite = 2L;
    private const long DeptDelete = 4L;
    private const long DeptCreateFiles = 8L;
    private const long DeptCreateFolders = 16L;
    private const long DeptRename = 32L;

    /// <summary>
    /// Checks whether the department default permission bitmask
    /// grants the requested <see cref="FilePermission"/>.
    /// </summary>
    public static bool Grants(long departmentPermission, FilePermission requested)
    {
        return requested switch
        {
            FilePermission.ListReadData =>
                (departmentPermission & DeptRead) != 0,

            FilePermission.CreateWriteData =>
                (departmentPermission & (DeptWrite | DeptCreateFiles | DeptCreateFolders)) != 0,

            FilePermission.Delete =>
                (departmentPermission & (DeptDelete | DeptRename)) != 0,

            // For any other/combined permission, check all relevant flags
            _ => CheckCombined(departmentPermission, requested)
        };
    }

    private static bool CheckCombined(long departmentPermission, FilePermission requested)
    {
        // If multiple flags are requested, all must be satisfied
        if (requested.HasFlag(FilePermission.ListReadData)
            && (departmentPermission & DeptRead) == 0)
            return false;

        if (requested.HasFlag(FilePermission.CreateWriteData)
            && (departmentPermission & (DeptWrite | DeptCreateFiles | DeptCreateFolders)) == 0)
            return false;

        if (requested.HasFlag(FilePermission.Delete)
            && (departmentPermission & (DeptDelete | DeptRename)) == 0)
            return false;

        return true;
    }
}