using System;
using System.Collections.Generic;
using System.Text;
using Kaimo_File_Server.Core.Security;

namespace Kaimo_File_Server.Core.Domain.Identity
{
    public class Role : Identity
    {
        /// <summary>
        /// Management permissions this role grants.
        /// Controls what administrative actions holders can perform
        /// within the scope of their assignment.
        /// </summary>
        public ManagementPermission ManagementPermissions { get; set; }
            = ManagementPermission.None;

        /// <summary>
        /// System roles (Administrator, User, etc.) cannot be deleted or renamed.
        /// Custom roles created by admins have IsSystemRole = false.
        /// </summary>
        public bool IsSystemRole { get; set; }

        protected Role() { }

        public Role(Guid id, string name,
            ManagementPermission managementPermissions = ManagementPermission.None,
            bool isSystemRole = false)
            : base(id, name)
        {
            ManagementPermissions = managementPermissions;
            IsSystemRole = isSystemRole;
        }
    }
}
