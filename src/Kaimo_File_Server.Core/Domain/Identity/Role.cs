using System;
using Kaimo_File_Server.Core.Security;

namespace Kaimo_File_Server.Core.Domain.Identity
{
    /// <summary>
    /// Defines a set of management permissions that can be assigned to users.
    ///
    /// Roles come in two flavours:
    ///   • <b>System roles</b> (e.g. "Administrator", "User") are seeded at
    ///     startup and cannot be deleted or renamed.
    ///   • <b>Custom roles</b> are created by administrators at runtime.
    ///
    /// The actual file-level permissions (read/write/delete …) live on ACLs;
    /// <see cref="ManagementPermissions"/> controls administrative actions
    /// such as creating users, managing shares, or editing groups.
    /// </summary>
    public class Role : Identity
    {
        /// <summary>
        /// Flags that control which administrative actions holders of this
        /// role may perform within the scope of their assignment.
        /// </summary>
        public ManagementPermission ManagementPermissions { get; set; }
            = ManagementPermission.None;

        /// <summary>
        /// When <c>true</c>, this role was seeded by the system and must not
        /// be deleted or renamed. Custom roles have this set to <c>false</c>.
        /// </summary>
        public bool IsSystemRole { get; set; }

        /// <summary>EF Core / serialization constructor.</summary>
        protected Role() { }

        /// <param name="id">Unique identifier.</param>
        /// <param name="name">Display name (e.g. "Department Admin").</param>
        /// <param name="managementPermissions">Administrative permissions granted by this role.</param>
        /// <param name="isSystemRole">Whether this is a built-in, non-deletable role.</param>
        public Role(
            Guid id,
            string name,
            ManagementPermission managementPermissions = ManagementPermission.None,
            bool isSystemRole = false)
            : base(id, name)
        {
            ManagementPermissions = managementPermissions;
            IsSystemRole = isSystemRole;
        }
    }
}