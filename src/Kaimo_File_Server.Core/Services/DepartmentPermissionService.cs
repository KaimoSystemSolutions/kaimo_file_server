using Kaimo_File_Server.Core.Repositories;

namespace Kaimo_File_Server.Core.Services;

/// <summary>
/// Resolves effective default file permissions for departments
/// by walking the hierarchy chain (OOP-style inheritance).
///
/// Key design: A share belongs to exactly ONE department (via direct FK).
/// Permission logic:
///   1. Load the share → read ShareDefinition.DepartmentId
///   2. Check if the user/group belongs to that department
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
///     └-- Department "Backend" (DefaultFilePermission = null → inherits Read|Write)
///
///   Share "Docs" → DepartmentId = Engineering
///   User "Alice" → member of "Engineering" → gets Read|Write on "Docs"
///   User "Bob"   → member of "Backend"     → gets nothing on "Docs"
///                   (Bob is NOT a direct member of Engineering)
/// </summary>
public class DepartmentPermissionService : IDepartmentPermissionService
{
    private readonly IDepartmentRepository _departmentRepo;
    private readonly IShareRepository _shareRepo;
    private readonly IGroupRepository _groupRepo;

    public DepartmentPermissionService(
        IDepartmentRepository departmentRepo,
        IShareRepository shareRepo,
        IGroupRepository groupRepo)
    {
        _departmentRepo = departmentRepo;
        _shareRepo = shareRepo;
        _groupRepo = groupRepo;
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
        // 1. Load the share → read its DepartmentId (direct FK)
        var share = await _shareRepo.GetByIdAsync(shareId);
        if (share == null) return 0;

        // 2. Check if user is a direct member of that department
        if (!await _departmentRepo.IsUserInDepartmentAsync(userId, share.DepartmentId))
            return 0;

        // 3. Resolve effective default (with hierarchy inheritance for the VALUE)
        return await GetEffectiveDefaultPermissionAsync(share.DepartmentId);
    }

    /// <inheritdoc />
    public async Task<long> GetEffectiveDefaultPermissionForGroupOnShareAsync(
        Guid groupId, Guid shareId)
    {
        // 1. Load the share → read its DepartmentId (direct FK)
        var share = await _shareRepo.GetByIdAsync(shareId);
        if (share == null) return 0;

        // 2. Load the group → read its DepartmentId (direct FK)
        var group = await _groupRepo.GetByIdAsync(groupId);
        if (group == null) return 0;

        // 3. Check if group belongs to the same department as the share
        if (group.DepartmentId != share.DepartmentId)
            return 0;

        // 4. Resolve effective default
        return await GetEffectiveDefaultPermissionAsync(share.DepartmentId);
    }
}