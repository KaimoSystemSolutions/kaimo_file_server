using Kaimo_File_Server.Core.Repositories;

namespace Kaimo_File_Server.Core.Services;

/// <summary>
/// Resolves effective default file permissions for departments
/// by walking the hierarchy chain (OOP-style inheritance).
///
/// Key simplification: A share is assigned to at most ONE department.
/// Permission logic:
///   1. Find the share's department (0 or 1)
///   2. Check if the user/group is a direct member of that department
///   3. If yes → resolve the department's effective default permission
///      (which may be inherited from a parent department)
///   4. If no match → 0 (no department-based access)
///
/// The hierarchy only affects the PERMISSION VALUE inheritance,
/// not the membership check. A user must be a direct member of
/// the share's department to get department-based file access.
///
/// Example:
///   Department "Engineering" (DefaultFilePermission = Read|Write)
///     └── Department "Backend" (DefaultFilePermission = null → inherits Read|Write)
///
///   Share "Docs" → assigned to "Engineering"
///   User "Alice" → member of "Engineering" → gets Read|Write on "Docs"
///   User "Bob"   → member of "Backend"     → gets nothing on "Docs"
///                   (Bob is NOT a direct member of Engineering)
/// </summary>
public class DepartmentPermissionService : IDepartmentPermissionService
{
    private readonly IDepartmentRepository _departmentRepo;

    public DepartmentPermissionService(IDepartmentRepository departmentRepo)
    {
        _departmentRepo = departmentRepo;
    }

    /// <inheritdoc />
    public async Task<long> GetEffectiveDefaultPermissionAsync(Guid departmentId)
    {
        var info = await GetPermissionInfoAsync(departmentId);
        return info.EffectivePermission;
    }

    /// <inheritdoc />
    public async Task<DepartmentPermissionInfo> GetPermissionInfoAsync(Guid departmentId)
    {
        var dept = await _departmentRepo.GetByIdAsync(departmentId);
        if (dept == null)
        {
            return new DepartmentPermissionInfo
            {
                EffectivePermission = 0,
                IsOwnPermission = false
            };
        }

        // Department defines its own default → use it
        if (dept.DefaultFilePermission.HasValue)
        {
            return new DepartmentPermissionInfo
            {
                EffectivePermission = dept.DefaultFilePermission.Value,
                IsOwnPermission = true
            };
        }

        // Walk up the ancestor chain to find the first non-null default
        var ancestors = await _departmentRepo.GetAncestorChainAsync(departmentId);

        foreach (var ancestor in ancestors)
        {
            if (ancestor.DefaultFilePermission.HasValue)
            {
                return new DepartmentPermissionInfo
                {
                    EffectivePermission = ancestor.DefaultFilePermission.Value,
                    IsOwnPermission = false,
                    InheritedFromDepartmentId = ancestor.Id,
                    InheritedFromDepartmentName = ancestor.Name
                };
            }
        }

        // No default in the entire chain
        return new DepartmentPermissionInfo
        {
            EffectivePermission = 0,
            IsOwnPermission = false
        };
    }

    /// <inheritdoc />
    public async Task<long> GetEffectiveDefaultPermissionForUserOnShareAsync(
        Guid userId, Guid shareId)
    {
        // 1. Find the share's department (max 1)
        var shareDepts = await _departmentRepo.GetDepartmentsForShareAsync(shareId);
        var shareDept = shareDepts.FirstOrDefault();
        if (shareDept == null) return 0; // Share not assigned to any department

        // 2. Check if user is a direct member of that department
        var userDepts = await _departmentRepo.GetDepartmentsForUserAsync(userId);
        if (!userDepts.Any(d => d.Id == shareDept.Id))
            return 0; // User is not in the share's department

        // 3. Resolve effective default (with hierarchy inheritance for the VALUE)
        return await GetEffectiveDefaultPermissionAsync(shareDept.Id);
    }

    /// <inheritdoc />
    public async Task<long> GetEffectiveDefaultPermissionForGroupOnShareAsync(
        Guid groupId, Guid shareId)
    {
        // 1. Find the share's department (max 1)
        var shareDepts = await _departmentRepo.GetDepartmentsForShareAsync(shareId);
        var shareDept = shareDepts.FirstOrDefault();
        if (shareDept == null) return 0;

        // 2. Check if group is a direct member of that department
        var groupDepts = await _departmentRepo.GetDepartmentsForGroupAsync(groupId);
        if (!groupDepts.Any(d => d.Id == shareDept.Id))
            return 0;

        // 3. Resolve effective default
        return await GetEffectiveDefaultPermissionAsync(shareDept.Id);
    }
}