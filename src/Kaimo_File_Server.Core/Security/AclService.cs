using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Microsoft.Extensions.DependencyInjection;
using System.Net.NetworkInformation;

namespace Kaimo_File_Server.Core.Security;

public class AclService : IAclService
{
    private readonly IServiceProvider _serviceProvider;

    public AclService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    // Parameterloser Konstruktor für Unit Tests (kein DB-Zugriff)
    public AclService() : this(null!)
    {
    }

    // ── Sync API für Unit Tests und direkte ACL-Auswertung ──

    /// <summary>
    /// Prüft Zugriff anhand einer bereits geladenen ACL-Liste (kein DB-Zugriff).
    /// </summary>
    public bool HasAccess(UserContext? userContext, FileMetadata file, FilePermission permission)
    {
        if (userContext == null)
            return false;

        var acl = file.Acl;
        if (acl == null || acl.Count == 0)
            return true;

        var userPrincipalIds = CollectPrincipalIds(userContext);

        // Deny takes precedence
        foreach (var entry in acl)
        {
            if (entry.EntryType != AclEntryType.Deny) continue;
            if (!userPrincipalIds.Contains(entry.PrincipalId)) continue;
            if ((entry.Permissions & permission) != 0) return false;
        }

        // Check Allow
        foreach (var entry in acl)
        {
            if (entry.EntryType != AclEntryType.Allow) continue;
            if (!userPrincipalIds.Contains(entry.PrincipalId)) continue;
            if ((entry.Permissions & permission) != 0) return true;
        }

        return false;
    }

    /// <summary>
    /// Filtert geerbte ACL-Einträge nach Vererbungsregeln (kein DB-Zugriff).
    /// Simuliert: "Welche Einträge eines Eltern-Ordners gelten für ein Kind?"
    /// </summary>
    public List<AccessEntry> GetEffectiveAcl(List<AccessEntry> parentAcl, bool isDirectory)
    {
        var result = new List<AccessEntry>();
        foreach (var entry in parentAcl)
        {
            if ((entry.Inheritance & AclInheritance.AllDescendants) != 0)
            {
                result.Add(entry);
                continue;
            }

            if (isDirectory && (entry.Inheritance & AclInheritance.SubFolders) != 0)
            {
                result.Add(entry);
                continue;
            }

            if (!isDirectory && (entry.Inheritance & AclInheritance.SubFiles) != 0)
            {
                result.Add(entry);
            }
        }
        return result;
    }

    public async Task RenameAclPathAsync(Guid shareId, string oldRelativePath, string newRelativePath)
    {
        var scope = _serviceProvider.CreateAsyncScope();
        var aclRepo = scope.ServiceProvider.GetRequiredService<IAclRepository>();

        await aclRepo.RenameFileMetadataPathsAsync(shareId, oldRelativePath, newRelativePath);
    }

    /// <summary>
    /// Deletes an ACL entry
    /// </summary>
    /// <param name="shareId">Guid of share, where ACL is based in</param>
    /// <param name="relativePath">Path from share to file/folder</param>
    public async Task DeleteAclAsync(Guid shareId, string relativePath)
    {
        var scope = _serviceProvider.CreateAsyncScope();
        var aclRepo = scope.ServiceProvider.GetRequiredService<IAclRepository>();

        // Get ACL Entries which shall be deleted
        var aclEntries = await aclRepo.GetAclsForPathsAsync(shareId, new List<string>() { relativePath });

        var listOfGuids = aclEntries
            .SelectMany(e => e.Acl
            .Select(a => a.Id))
            .Distinct()
            .ToList();

        foreach (var entryGuid in listOfGuids)
        {
            await aclRepo.DeleteAsync(entryGuid);
        }
    }

    // ── Async API für Produktion (mit DB) ──

    public async Task<bool> HasAccessAsync(
        UserContext userContext, Guid shareId,
        string relativePath, bool isDirectory, FilePermission permission)
    {
        if (userContext == null)
            return false;

        var normalizedPath = ShareRelativePath.Normalize(relativePath);
        var userPrincipalIds = CollectPrincipalIds(userContext);
        var effectiveAcl = await ResolveEffectiveAclAsync(shareId, normalizedPath, isDirectory);

        if (effectiveAcl.Count == 0)
            return true;

        foreach (var entry in effectiveAcl)
        {
            if (entry.EntryType != AclEntryType.Deny) continue;
            if (!userPrincipalIds.Contains(entry.PrincipalId)) continue;
            if ((entry.Permissions & permission) != 0) return false;
        }

        foreach (var entry in effectiveAcl)
        {
            if (entry.EntryType != AclEntryType.Allow) continue;
            if (!userPrincipalIds.Contains(entry.PrincipalId)) continue;
            if ((entry.Permissions & permission) != 0) return true;
        }

        return false;
    }

    private async Task<List<AccessEntry>> ResolveEffectiveAclAsync(
        Guid shareId, string normalizedPath, bool isDirectory)
    {
        var pathsToCheck = ShareRelativePath.BuildHierarchy(normalizedPath);

        using var scope = _serviceProvider.CreateScope();
        var aclRepo = scope.ServiceProvider.GetRequiredService<IAclRepository>();
        var allAcls = await aclRepo.GetAclsForPathsAsync(shareId, pathsToCheck);

        var effective = new List<AccessEntry>();
        var targetPath = pathsToCheck[^1];
        var targetDepth = ShareRelativePath.GetDepth(targetPath);

        foreach (var (path, pathIsDir, acl) in allAcls)
        {
            if (acl == null || acl.Count == 0)
                continue;

            var isDirect = (path == targetPath);

            foreach (var entry in acl)
            {
                if (isDirect)
                {
                    if ((entry.Inheritance & AclInheritance.ThisFolder) != 0)
                        effective.Add(entry);
                }
                else
                {
                    if (AppliesToDescendant(entry, isDirectory, path, targetDepth))
                        effective.Add(entry);
                }
            }
        }

        return effective;
    }

    private static bool AppliesToDescendant(
        AccessEntry entry, bool targetIsDirectory,
        string sourcePath, int targetDepth)
    {
        if ((entry.Inheritance & AclInheritance.AllDescendants) != 0)
            return true;

        var sourceDepth = ShareRelativePath.GetDepth(sourcePath);
        var isDirectChild = (targetDepth == sourceDepth + 1);
        if (!isDirectChild)
            return false;

        if (targetIsDirectory && (entry.Inheritance & AclInheritance.SubFolders) != 0)
            return true;
        if (!targetIsDirectory && (entry.Inheritance & AclInheritance.SubFiles) != 0)
            return true;

        return false;
    }

    private static HashSet<Guid> CollectPrincipalIds(UserContext userContext)
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