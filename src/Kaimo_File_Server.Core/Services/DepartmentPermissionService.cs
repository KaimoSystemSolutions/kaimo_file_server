using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services;

namespace Kaimo_File_Server.Core.Services;

/// <summary>
/// Resolves effective default file permissions for departments
/// by walking the hierarchy chain (OOP-style inheritance).
///
/// Permission inheritance:
///   Department "Backend" (DefaultFilePermission = null, Parent = "Engineering")
///   Department "Engineering" (DefaultFilePermission = Read|Write)
///   → Backend's effective permission = Read|Write (inherited from Engineering)
///
///   Department "HR" (DefaultFilePermission = Read, Parent = null)
///   → HR's effective permission = Read (own)
///
///   Department "NewDept" (DefaultFilePermission = null, Parent = null)
///   → NewDept's effective permission = None (no inheritance chain)
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

        // If this department has its own default, use it directly
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

        // No default found in the entire chain
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
        // Find departments the user belongs to
        var userDepts = await _departmentRepo.GetDepartmentsForUserAsync(userId);
        if (userDepts.Count == 0) return 0;

        // Find departments the share belongs to
        var shareDepts = await _departmentRepo.GetDepartmentsForShareAsync(shareId);
        if (shareDepts.Count == 0) return 0;

        var shareDeptIds = shareDepts.Select(d => d.Id).ToHashSet();
        long combined = 0;

        foreach (var userDept in userDepts)
        {
            // Direct match: user and share are in the same department
            if (shareDeptIds.Contains(userDept.Id))
            {
                var effective = await GetEffectiveDefaultPermissionAsync(userDept.Id);
                combined |= effective;
                continue;
            }

            // Ancestor match: the share's department is an ancestor of the user's department
            // (user in "Backend", share assigned to "Engineering" which is parent)
            var ancestors = await _departmentRepo.GetAncestorChainAsync(userDept.Id);
            foreach (var ancestor in ancestors)
            {
                if (shareDeptIds.Contains(ancestor.Id))
                {
                    // Use the user's department's effective permission (not the ancestor's),
                    // because the user's more specific department may override
                    var effective = await GetEffectiveDefaultPermissionAsync(userDept.Id);
                    combined |= effective;
                    break;
                }
            }

            // Descendant match: the user's department is an ancestor of the share's department
            // (user in "Engineering", share assigned to "Backend" which is child)
            var descendants = await _departmentRepo.GetDescendantIdsAsync(userDept.Id);
            foreach (var shareDept in shareDepts)
            {
                if (descendants.Contains(shareDept.Id))
                {
                    var effective = await GetEffectiveDefaultPermissionAsync(userDept.Id);
                    combined |= effective;
                    break;
                }
            }
        }

        return combined;
    }

    /// <inheritdoc />
    public async Task<long> GetEffectiveDefaultPermissionForGroupOnShareAsync(
        Guid groupId, Guid shareId)
    {
        var groupDepts = await _departmentRepo.GetDepartmentsForGroupAsync(groupId);
        if (groupDepts.Count == 0) return 0;

        var shareDepts = await _departmentRepo.GetDepartmentsForShareAsync(shareId);
        if (shareDepts.Count == 0) return 0;

        var shareDeptIds = shareDepts.Select(d => d.Id).ToHashSet();
        long combined = 0;

        foreach (var groupDept in groupDepts)
        {
            if (shareDeptIds.Contains(groupDept.Id))
            {
                var effective = await GetEffectiveDefaultPermissionAsync(groupDept.Id);
                combined |= effective;
                continue;
            }

            var ancestors = await _departmentRepo.GetAncestorChainAsync(groupDept.Id);
            foreach (var ancestor in ancestors)
            {
                if (shareDeptIds.Contains(ancestor.Id))
                {
                    var effective = await GetEffectiveDefaultPermissionAsync(groupDept.Id);
                    combined |= effective;
                    break;
                }
            }

            var descendants = await _departmentRepo.GetDescendantIdsAsync(groupDept.Id);
            foreach (var shareDept in shareDepts)
            {
                if (descendants.Contains(shareDept.Id))
                {
                    var effective = await GetEffectiveDefaultPermissionAsync(groupDept.Id);
                    combined |= effective;
                    break;
                }
            }
        }

        return combined;
    }
}