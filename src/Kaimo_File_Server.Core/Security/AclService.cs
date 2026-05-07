using Kaimo_File_Server.Core.Domain;

namespace Kaimo_File_Server.Core.Security
{
    public class AclService : IAclService
    {
        public bool HasAccess(UserContext userContext, FileMetadata file, FilePermission permission)
        {
            // ── DENY BY DEFAULT wenn kein User-Context vorhanden ──
            // Das ist die sicherste Variante: ohne authentifizierten User kein Zugriff.
            if (userContext == null)
                return false;

            // Wenn keine ACLs definiert sind, erlauben wir Zugriff (offenes System).
            // TODO: Sobald ACLs aus der DB geladen werden, diesen Fallback überdenken.
            //       Mögliche Strategie: Default-ACL pro Share definieren.
            if (file.Acl == null || file.Acl.Count == 0)
                return true;

            var userPrincipalIds = GetPrincipalIds(userContext);

            // Deny hat IMMER Vorrang (Windows-NTFS-Semantik)
            foreach (var entry in file.Acl)
            {
                if (entry.EntryType != AclEntryType.Deny)
                    continue;

                if (!userPrincipalIds.Contains(entry.PrincipalId))
                    continue;

                if ((entry.Permissions & permission) != 0)
                    return false;
            }

            // Dann Allow prüfen
            foreach (var entry in file.Acl)
            {
                if (entry.EntryType != AclEntryType.Allow)
                    continue;

                if (!userPrincipalIds.Contains(entry.PrincipalId))
                    continue;

                if ((entry.Permissions & permission) != 0)
                    return true;
            }

            // Kein Match = kein Zugriff (Whitelist-Prinzip)
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