using Kaimo_File_Server_Core.Core.Domain;
using System;
using System.Collections.Generic;
using System.Text;
using static Kaimo_File_Server_Core.Core.Security.FilePermission;

namespace Kaimo_File_Server_Core.Core.Security
{
    public class AclService : IAclService
    {
        public bool HasAccess(UserContext userContext, FileMetadata file, FilePermission permission)
        {
            if (userContext == null || file.Acl == null || file.Acl.Count == 0)
                return true;

            var userPrincipalIds = GetPrincipalIds(userContext);

            // Deny hat IMMER Vorrang
            foreach (var entry in file.Acl)
            {
                if (entry.EntryType != AclEntryType.Deny)
                    continue;

                if (!userPrincipalIds.Contains(entry.PrincipalId))
                    continue;

                if ((entry.Permissions & permission) != 0)
                    return false; // explizit verweigert
            }

            // Dann Allow prüfen
            foreach (var entry in file.Acl)
            {
                if (entry.EntryType != AclEntryType.Allow)
                    continue;

                if (!userPrincipalIds.Contains(entry.PrincipalId))
                    continue;

                if ((entry.Permissions & permission) != 0)
                    return true; // explizit erlaubt
            }

            // Kein Match = kein Zugriff (Whitelist)
            return false;
        }

        public List<AccessEntry> GetEffectiveAcl(List<AccessEntry> parentAcl, bool isDirectory)
        {
            var inherited = new List<AccessEntry>();

            foreach (var entry in parentAcl)
            {
                bool applies = false;

                if ((entry.Inheritance & AclInheritance.AllDescendants) != 0)
                    applies = true;
                else if (isDirectory && (entry.Inheritance & AclInheritance.SubFolders) != 0)
                    applies = true;
                else if (!isDirectory && (entry.Inheritance & AclInheritance.SubFiles) != 0)
                    applies = true;

                if (applies)
                    inherited.Add(entry);
            }

            return inherited;
        }

        private HashSet<Guid> GetPrincipalIds(UserContext userContext)
        {
            var ids = new HashSet<Guid> { userContext.User.Id };

            if (userContext.Groups != null)
                foreach (var g in userContext.Groups)
                    ids.Add(g.Id);

            if (userContext.Roles != null)
                foreach (var r in userContext.Roles)
                    ids.Add(r.Id);

            return ids;
        }
    }
}
