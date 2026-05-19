using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace Kaimo_File_Server.Core.Security
{
    public class AclService : IAclService
    {
        private readonly IServiceProvider _serviceProvider;

        public AclService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public async Task<bool> HasAccessAsync(UserContext userContext, Guid shareId,
            string relativePath, bool isDirectory, FilePermission permission)
        {
            if (userContext == null) return false;

            var userPrincipalIds = GetPrincipalIds(userContext);
            var effectiveAcl = await GetEffectiveAclAsync(shareId, relativePath, isDirectory);

            // Keine ACLs = offenes System
            if (effectiveAcl.Count == 0)
                return true;

            // Deny hat Vorrang (NTFS-Semantik)
            foreach (var entry in effectiveAcl)
            {
                if (entry.EntryType != AclEntryType.Deny) continue;
                if (!userPrincipalIds.Contains(entry.PrincipalId)) continue;
                if ((entry.Permissions & permission) != 0) return false;
            }

            // Allow prüfen
            foreach (var entry in effectiveAcl)
            {
                if (entry.EntryType != AclEntryType.Allow) continue;
                if (!userPrincipalIds.Contains(entry.PrincipalId)) continue;
                if ((entry.Permissions & permission) != 0) return true;
            }

            // Kein Match = kein Zugriff
            return false;
        }

        /// <summary>
        /// Sammelt ACLs vom Zielobjekt + allen Elternordnern.
        /// Beispiel: Pfad "docs/sub/file.txt"
        /// Prüft: "" (Share-Root) → "docs" → "docs/sub" → "docs/sub/file.txt"
        /// </summary>
        private async Task<List<AccessEntry>> GetEffectiveAclAsync(
            Guid shareId, string relativePath, bool isDirectory)
        {
            var pathsToCheck = BuildPathHierarchy(relativePath);

            // Eigener Scope pro Aufruf — sicher für langlebige SMB-Instanzen
            using var scope = _serviceProvider.CreateScope();
            var aclRepo = scope.ServiceProvider.GetRequiredService<IAclRepository>();
            var allAcls = await aclRepo.GetAclsForPathsAsync(shareId, pathsToCheck);

            var effective = new List<AccessEntry>();
            var targetPath = pathsToCheck[^1];
            var targetDepth = targetPath == "" ? 0 : targetPath.Split('/').Length;

            foreach (var (path, pathIsDir, acl) in allAcls)
            {
                if (acl == null || acl.Count == 0) continue;
                var isDirect = path == targetPath;

                foreach (var entry in acl)
                {
                    if (isDirect)
                    {
                        // Direkte ACLs: ThisFolder muss gesetzt sein
                        if ((entry.Inheritance & AclInheritance.ThisFolder) != 0)
                            effective.Add(entry);
                    }
                    else
                    {
                        // Vererbte ACLs
                        if (AppliesAsInherited(entry, isDirectory, path, targetDepth))
                            effective.Add(entry);
                    }
                }
            }

            return effective;
        }

        /// <summary>
        /// Baut die Pfad-Hierarchie auf: "", "docs", "docs/sub", "docs/sub/file.txt"
        /// </summary>
        private static List<string> BuildPathHierarchy(string relativePath)
        {
            var paths = new List<string> { "" }; // Share-Root
            if (string.IsNullOrEmpty(relativePath)) return paths;

            var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < segments.Length; i++)
                paths.Add(string.Join("/", segments.Take(i + 1)));

            return paths;
        }

        /// <summary>
        /// Prüft ob ein ACL-Eintrag eines Elternordners auf das Zielobjekt vererbt wird.
        /// </summary>
        private static bool AppliesAsInherited(AccessEntry entry, bool targetIsDirectory,
            string sourcePath, int targetDepth)
        {
            // AllDescendants = gilt für alles darunter, egal wie tief
            if ((entry.Inheritance & AclInheritance.AllDescendants) != 0)
                return true;

            // SubFolders/SubFiles = nur direkte Kinder
            var sourceDepth = sourcePath == "" ? 0 : sourcePath.Split('/').Length;
            var isDirectChild = targetDepth == sourceDepth + 1;
            if (!isDirectChild) return false;

            if (targetIsDirectory && (entry.Inheritance & AclInheritance.SubFolders) != 0)
                return true;
            if (!targetIsDirectory && (entry.Inheritance & AclInheritance.SubFiles) != 0)
                return true;

            return false;
        }

        private static HashSet<Guid> GetPrincipalIds(UserContext userContext)
        {
            var ids = new HashSet<Guid> { userContext.User.Id };
            if (userContext.Groups != null)
                foreach (var g in userContext.Groups) ids.Add(g.Id);
            if (userContext.Roles != null)
                foreach (var r in userContext.Roles) ids.Add(r.Id);
            return ids;
        }
    }
}