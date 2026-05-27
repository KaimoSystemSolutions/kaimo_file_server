using System;
using System.Collections.Generic;
using System.Text;
using Kaimo_File_Server.Core.Domain.Department;

namespace Kaimo_File_Server.Core.Domain.Identity
{
    public class UserContext
    {
        public User User { get; init; }

        public HashSet<Group> Groups { get; init; }
        public HashSet<Role> Roles { get; init; }
        public HashSet<string> Permissions { get; init; }

        /// <summary>
        /// Departments this user belongs to.
        /// Used by ManagementAuthService to determine scoped admin rights.
        /// </summary>
        public HashSet<Department.Department> Departments { get; init; }

        public UserContext(User user, HashSet<Group> groups, HashSet<Role> roles,
            HashSet<string> permissions, HashSet<Department.Department>? departments = null)
        {
            User = user;
            Groups = groups;
            Roles = roles;
            Permissions = permissions;
            Departments = departments ?? new HashSet<Department.Department>();
        }
    }
}
