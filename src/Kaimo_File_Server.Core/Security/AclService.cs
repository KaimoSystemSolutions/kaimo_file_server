using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace Kaimo_File_Server.Core.Security;

public class AclService : IAclService
{
    private readonly IServiceProvider _serviceProvider;

    public AclService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider
            ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    // ── Sync API for unit tests and direct ACL evaluation ──

    /// <summary>
    /// Checks access against an already-loaded ACL list (no DB access).
    /// </summary>
    public bool HasAccess(UserContext? userContext, FileMetadata file, FilePermission permission)
    {
        if (userContext == null)
            return false;

        var acl = file.Acl;
        if (acl == null || acl.Count == 0)
            return true;

        var userPrincipalIds = CollectPrincipalIds(userContext);
        return EvaluateAccess(acl, userPrincipalIds, permission);
    }

    /// <summary>
    /// Filters inherited ACL entries by inheritance rules (no DB access).
    /// Simulates: "Which parent-folder entries apply to a child?"
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
        await using var scope = _serviceProvider.CreateAsyncScope();
        var aclRepo = scope.ServiceProvider.GetRequiredService<IAclRepository>();

        await aclRepo.RenameFileMetadataPathsAsync(shareId, oldRelativePath, newRelativePath);
    }

    public async Task DeleteAclAsync(Guid shareId, string relativePath)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var aclRepo = scope.ServiceProvider.GetRequiredService<IAclRepository>();

        var aclEntries = await aclRepo.GetAclsForPathsAsync(
            shareId, new List<string> { relativePath });

        var entryIds = aclEntries
            .SelectMany(e => e.Acl.Select(a => a.Id))
            .Distinct()
            .ToList();

        foreach (var entryGuid in entryIds)
        {
            await aclRepo.DeleteAsync(entryGuid);
        }
    }

    // ── Async API — single-item (delegates to batch) ──

    public async Task<bool> HasAccessAsync(
        UserContext userContext, Guid shareId,
        string relativePath, bool isDirectory, FilePermission permission)
    {
        if (userContext == null)
            return false;

        var normalizedPath = ShareRelativePath.Normalize(relativePath);
        var userPrincipalIds = CollectPrincipalIds(userContext);

        var hierarchy = ShareRelativePath.BuildHierarchy(normalizedPath);

        using var scope = _serviceProvider.CreateScope();
        var aclRepo = scope.ServiceProvider.GetRequiredService<IAclRepository>();
        var allAcls = await aclRepo.GetAclsForPathsAsync(shareId, hierarchy);

        var aclByPath = IndexAclsByPath(allAcls);
        var effectiveAcl = ResolveEffectiveAclFromCache(
            aclByPath, hierarchy, normalizedPath, isDirectory);

        return EvaluateAccess(effectiveAcl, userPrincipalIds, permission);
    }

    // ── Async API — batch (single DB round trip for N items) ──

    /// <summary>
    /// Checks access for multiple items in a single DB round trip.
    ///
    /// All hierarchy paths (item + ancestors) are collected, de-duplicated,
    /// and fetched in one query. Effective ACLs are then resolved in-memory.
    ///
    /// Typical use: filtering a directory listing of 500 files = 1 DB call
    /// instead of 500.
    /// </summary>
    public async Task<Dictionary<string, bool>> HasAccessBatchAsync(
        UserContext userContext, Guid shareId,
        IReadOnlyList<(string relativePath, bool isDirectory)> items,
        FilePermission permission)
    {
        var results = new Dictionary<string, bool>(
            items.Count, StringComparer.OrdinalIgnoreCase);

        if (userContext == null)
        {
            foreach (var (path, _) in items)
                results[ShareRelativePath.Normalize(path)] = false;
            return results;
        }

        if (items.Count == 0)
            return results;

        var userPrincipalIds = CollectPrincipalIds(userContext);

        // ── 1. Build per-item hierarchies, collect all unique paths ──
        var allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var perItem = new List<(string normalized, bool isDir, List<string> hierarchy)>(items.Count);

        foreach (var (path, isDir) in items)
        {
            var normalized = ShareRelativePath.Normalize(path);
            var hierarchy = ShareRelativePath.BuildHierarchy(normalized);
            perItem.Add((normalized, isDir, hierarchy));

            foreach (var h in hierarchy)
                allPaths.Add(h);
        }

        // ── 2. Single DB round trip ──
        using var scope = _serviceProvider.CreateScope();
        var aclRepo = scope.ServiceProvider.GetRequiredService<IAclRepository>();
        var allAcls = await aclRepo.GetAclsForPathsAsync(shareId, allPaths.ToList());

        // ── 3. Index for O(1) lookup ──
        var aclByPath = IndexAclsByPath(allAcls);

        // ── 4. Evaluate each item in-memory ──
        foreach (var (normalized, isDir, hierarchy) in perItem)
        {
            var effectiveAcl = ResolveEffectiveAclFromCache(
                aclByPath, hierarchy, normalized, isDir);
            results[normalized] = EvaluateAccess(effectiveAcl, userPrincipalIds, permission);
        }

        return results;
    }

    // ── Shared evaluation logic ──

    /// <summary>
    /// Indexes ACL query results by path for O(1) lookup.
    /// </summary>
    private static Dictionary<string, List<AccessEntry>> IndexAclsByPath(
        List<(string Path, bool IsDir, List<AccessEntry> Acl)> aclResults)
    {
        var index = new Dictionary<string, List<AccessEntry>>(
            aclResults.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var (path, _, acl) in aclResults)
        {
            if (acl != null && acl.Count > 0)
                index[path] = acl;
        }

        return index;
    }

    /// <summary>
    /// Resolves effective ACL for a target path from a pre-loaded ACL cache.
    /// Walks the hierarchy from root to target, applying inheritance rules.
    /// </summary>
    private static List<AccessEntry> ResolveEffectiveAclFromCache(
        Dictionary<string, List<AccessEntry>> aclByPath,
        List<string> hierarchy, string targetPath, bool targetIsDirectory)
    {
        var effective = new List<AccessEntry>();
        var targetDepth = ShareRelativePath.GetDepth(targetPath);

        foreach (var path in hierarchy)
        {
            if (!aclByPath.TryGetValue(path, out var acl))
                continue;

            var isDirect = string.Equals(path, targetPath, StringComparison.OrdinalIgnoreCase);

            foreach (var entry in acl)
            {
                if (isDirect)
                {
                    if ((entry.Inheritance & AclInheritance.ThisFolder) != 0)
                        effective.Add(entry);
                }
                else
                {
                    if (AppliesToDescendant(entry, targetIsDirectory, path, targetDepth))
                        effective.Add(entry);
                }
            }
        }

        return effective;
    }

    /// <summary>
    /// Evaluates deny-first, then allow against the user's principal IDs.
    /// Empty ACL = restricted access (no allow configured).
    /// </summary>
    private static bool EvaluateAccess(
        List<AccessEntry> effectiveAcl,
        HashSet<Guid> userPrincipalIds,
        FilePermission permission)
    {
        if (effectiveAcl.Count == 0)
            return false;

        // Deny takes precedence
        foreach (var entry in effectiveAcl)
        {
            if (entry.EntryType != AclEntryType.Deny) continue;
            if (!userPrincipalIds.Contains(entry.PrincipalId)) continue;
            if ((entry.Permissions & permission) != 0) return false;
        }

        // Check Allow
        foreach (var entry in effectiveAcl)
        {
            if (entry.EntryType != AclEntryType.Allow) continue;
            if (!userPrincipalIds.Contains(entry.PrincipalId)) continue;
            if ((entry.Permissions & permission) != 0) return true;
        }

        return false;
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