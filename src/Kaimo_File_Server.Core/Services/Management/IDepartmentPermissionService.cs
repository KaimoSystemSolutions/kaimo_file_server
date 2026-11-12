using Kaimo_File_Server.Core.Domain.Department;

namespace Kaimo_File_Server.Core.Services.Management
{
    /// <summary>
    /// Resolves the effective default file permissions for departments,
    /// taking hierarchy inheritance into account.
    ///
    /// Inheritance follows OOP semantics:
    ///   - A department can define its own DefaultFilePermission (override)
    ///   - If null, it inherits from its parent department
    ///   - This walks up the chain until a non-null value is found or the root is reached
    ///   - Root departments with null default → no default permissions (None)
    ///
    /// Integration with the ACL system:
    ///   When resolving file access for a user on a share, the ACL service should
    ///   call <see cref="GetEffectiveDefaultPermissionForUserOnShareAsync"/> to check
    ///   if the user gets baseline access via department membership.
    ///   This permission is additive — it adds to any explicit ACL entries.
    /// </summary>
    public interface IDepartmentPermissionService
    {
        /// <summary>
        /// Resolves the effective default file permission for a department
        /// by walking up the hierarchy until a non-null value is found.
        ///
        /// Returns 0 (None) if no department in the chain defines a default.
        /// </summary>
        Task<long> GetEffectiveDefaultPermissionAsync(Guid departmentId);

        /// <summary>
        /// Returns the effective default file permission a user gets on a specific share
        /// through department membership.
        ///
        /// Logic:
        ///   1. Find all departments the user belongs to
        ///   2. Find all departments the share belongs to
        ///   3. For each overlapping department, resolve its effective default permission
        ///   4. Combine (OR) all resolved permissions — user gets the union
        ///
        /// Returns 0 (None) if no department grants default access.
        /// </summary>
        Task<long> GetEffectiveDefaultPermissionForUserOnShareAsync(
            Guid userId, Guid shareId);

        /// <summary>
        /// Returns the effective default file permission a group gets on a specific share
        /// through department membership.
        /// Same logic as user variant but checks group-department membership.
        /// </summary>
        Task<long> GetEffectiveDefaultPermissionForGroupOnShareAsync(
            Guid groupId, Guid shareId);

        /// <summary>
        /// Shows the inheritance chain for a department's default permission.
        /// Returns (effectivePermission, sourceDepartmentId, sourceDepartmentName)
        /// where source indicates which ancestor defined the permission.
        /// Useful for the admin UI to show "Geerbt von: Engineering".
        /// </summary>
        Task<DepartmentPermissionInfo> GetPermissionInfoAsync(Guid departmentId);
    }
}
