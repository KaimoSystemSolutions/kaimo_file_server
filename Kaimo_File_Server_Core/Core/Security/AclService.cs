using Kaimo_File_Server_Core.Core.Domain;
using System;
using System.Collections.Generic;
using System.Text;
using static Kaimo_File_Server_Core.Core.Security.FilePermissions;

namespace Kaimo_File_Server_Core.Core.Security
{
    internal class AclService : IAclService
    {
        public bool HasAccess(UserContext userContext, FileMetadata file, FilePermission permission)
        {
            // TODO: Kein User oder keine ACL = alles erlaubt (testweise)
            if (userContext == null || file.Acl == null || file.Acl.Count == 0)
                return true;

            foreach (AccessEntry entry in file.Acl)
            {
                // If the user has access through group, role, or direct user permission
                if (userContext.Groups.Any(g => g.Id == entry.PrincipalId) || 
                    userContext.Roles.Any(r => r.Id == entry.PrincipalId) ||
                    userContext.User.Id == entry.PrincipalId)
                {
                
                    // If the permission is granted, return true
                    if ((entry.Permissions & permission) != 0)
                        return true;
                }
            }

            return false;
        }
    }
}
