using Kaimo_File_Server_Core.Core.Domain.Identity;
using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server_Core.Core.Domain
{
    public class UserContext
    {
        public User User { get; init; }

        public HashSet<Group> Groups { get; init; }
        public HashSet<Role> Roles { get; init; }
        public HashSet<string> Permissions { get; init; }


        public UserContext(User user, HashSet<Group> groups, HashSet<Role> roles, HashSet<string> permissions)
        {
            User = user;
            Groups = groups;
            Roles = roles;
            Permissions = permissions;
        }
    }
}
