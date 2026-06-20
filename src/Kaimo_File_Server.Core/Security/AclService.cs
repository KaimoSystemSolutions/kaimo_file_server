using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace Kaimo_File_Server.Core.Security;

/// <summary>
/// Unified ACL evaluation service with integrated department default permissions.
///
/// Access evaluation priority (highest to lowest):
///   1. Explicit Deny ACL entries  → immediately denies access
///   2. Explicit Allow ACL entries → immediately grants access
///   3. Department Default         → grants baseline access if user is in the share's department
///   4. No match                   → access denied
///
/// Department defaults are evaluated at RUNTIME as a "virtual allow" layer.
/// No AccessEntry records are created — this means:
///   - Adding a user to a department instantly grants default access (no ACL rebuild)
///   - Changing a department's default instantly affects all members (no ACL rebuild)
///   - Explicit Deny entries always override department defaults
///   - The department hierarchy is walked to resolve inherited defaults
///
/// The department default is resolved ONCE per share (not per file),
/// making batch operations efficient: 1 extra query regardless of item count.
/// </summary>
public class AclService : IAclService
{
    private readonly IServiceProvider _serviceProvider;

    public AclService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider
            ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    // ══════════════════════════════════════════
    //  Sync API (unit tests, pre-loaded ACLs)
    // ══════════════════════════════════════════

    /// <summary>
    /// Checks access against an already-loaded ACL list (no DB access).
    /// Does NOT include department defaults — use the async methods for full evaluation.
    /// </summary>
    public bool HasAccess(UserContext? userContext, FileMetadata file, FilePermission permission)
    {
        if (userContext == null)
            return false;

        var acl = file.Acl;
        if (acl == null || acl.Count == 0)
            return true;

        var userPrincipalIds = CollectPrincipalIds(userContext);
        return EvaluateExplicitOnly(acl, userPrincipalIds, permission);
    }

    /// <summary>
    /// Filters inherited ACL entries by inheritance rules (no DB access).
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

    // ══════════════════════════════════════════
    //  Path management
    // ══════════════════════════════════════════

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

    // ══════════════════════════════════════════
    //  Async API — single item
    // ══════════════════════════════════════════

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
        var sp = scope.ServiceProvider;

        var aclRepo = sp.GetRequiredService<IAclRepository>();
        var allAcls = await aclRepo.GetAclsForPathsAsync(shareId, hierarchy);

        var aclByPath = IndexAclsByPath(allAcls);
        var effectiveAcl = ResolveEffectiveAclFromCache(
            aclByPath, hierarchy, normalizedPath, isDirectory);

        // Resolve department default ONCE for this share
        var deptDefault = await ResolveDepartmentDefaultAsync(userContext, shareId, sp);

        return EvaluateAccess(effectiveAcl, userPrincipalIds, permission, deptDefault);
    }

    // ══════════════════════════════════════════
    //  Async API — batch (single DB round trip)
    // ══════════════════════════════════════════

    /// <summary>
    /// Checks access for multiple items in a single DB round trip.
    ///
    /// Department defaults are resolved once per share (O(1)),
    /// then applied to each item alongside explicit ACLs.
    ///
    /// Performance: O(1) DB queries for ACLs + O(1) for department default
    /// regardless of item count.
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

        // -- 1. Build per-item hierarchies, collect all unique paths --
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

        // -- 2. Single DB round trip for ACLs --
        using var scope = _serviceProvider.CreateScope();
        var sp = scope.ServiceProvider;

        var aclRepo = sp.GetRequiredService<IAclRepository>();
        var allAcls = await aclRepo.GetAclsForPathsAsync(shareId, allPaths.ToList());

        // -- 3. Resolve department default ONCE for the entire batch --
        var deptDefault = await ResolveDepartmentDefaultAsync(userContext, shareId, sp);

        // -- 4. Index ACLs for O(1) lookup --
        var aclByPath = IndexAclsByPath(allAcls);

        // -- 5. Evaluate each item in-memory --
        foreach (var (normalized, isDir, hierarchy) in perItem)
        {
            var effectiveAcl = ResolveEffectiveAclFromCache(
                aclByPath, hierarchy, normalized, isDir);
            results[normalized] = EvaluateAccess(
                effectiveAcl, userPrincipalIds, permission, deptDefault);
        }

        return results;
    }

    // ══════════════════════════════════════════
    //  Department default resolution
    // ══════════════════════════════════════════

    /// <summary>
    /// Resolves the effective department default FilePermission for a user on a share.
    ///
    /// Returns FilePermission.None if:
    ///   - The share doesn't exist
    ///   - The user is not a member of the share's department
    ///   - No department in the hierarchy defines a default permission
    ///
    /// This is called ONCE per share, not per file. The result is reused
    /// for all items in a batch operation.
    /// </summary>
    private static async Task<FilePermission> ResolveDepartmentDefaultAsync(
        UserContext userContext, Guid shareId, IServiceProvider sp)
    {
        var shareRepo = sp.GetRequiredService<IShareRepository>();
        var share = await shareRepo.GetByIdAsync(shareId);
        if (share == null)
            return FilePermission.None;

        var deptRepo = sp.GetRequiredService<IDepartmentRepository>();

        // Membership is hierarchy-aware: a member of a sub-department also belongs
        // to the parent organisation, so they get access to shares owned by the
        // share's department OR any ancestor of their own department.
        // Equivalent: the user is a member of the share's department or of any
        // DESCENDANT of it.
        if (!await IsMemberOfDepartmentOrDescendantAsync(userContext, share.DepartmentId, deptRepo))
            return FilePermission.None;

        // The permission VALUE always comes from the SHARE's department (walking
        // up for inheritance). So a share owned by a parent department applies the
        // parent's default even to sub-department members, while a share owned by
        // the sub-department applies the sub-department's own default (or inherits).
        return await ResolveEffectiveDeptPermissionAsync(share.DepartmentId, deptRepo);
    }

    /// <summary>
    /// True if any of the user's departments is the share's department itself
    /// or a descendant of it (hierarchy-aware membership).
    /// </summary>
    private static async Task<bool> IsMemberOfDepartmentOrDescendantAsync(
        UserContext userContext, Guid shareDepartmentId, IDepartmentRepository deptRepo)
    {
        if (userContext.Departments == null || userContext.Departments.Count == 0)
            return false;

        if (userContext.Departments.Any(d => d.Id == shareDepartmentId))
            return true;

        var descendants = await deptRepo.GetDescendantIdsAsync(shareDepartmentId);
        return userContext.Departments.Any(d => descendants.Contains(d.Id));
    }

    /// <summary>
    /// Walks the department hierarchy upward to find the first non-null
    /// DefaultFilePermission. This implements OOP-style inheritance:
    ///   - Child department with own default → uses its own
    ///   - Child department with null → inherits from parent
    ///   - Root department with null → FilePermission.None
    ///
    /// The value is stored as long? but contains FilePermission flags directly.
    /// </summary>
    private static async Task<FilePermission> ResolveEffectiveDeptPermissionAsync(
        Guid departmentId, IDepartmentRepository deptRepo)
    {
        var dept = await deptRepo.GetByIdAsync(departmentId);
        if (dept == null)
            return FilePermission.None;

        // This department defines its own default
        if (dept.DefaultFilePermission.HasValue)
            return (FilePermission)dept.DefaultFilePermission.Value;

        // Walk up ancestor chain
        var ancestors = await deptRepo.GetAncestorChainAsync(departmentId);
        foreach (var ancestor in ancestors)
        {
            if (ancestor.DefaultFilePermission.HasValue)
                return (FilePermission)ancestor.DefaultFilePermission.Value;
        }

        return FilePermission.None;
    }

    // ══════════════════════════════════════════
    //  Core evaluation logic
    // ══════════════════════════════════════════

    /// <summary>
    /// Unified access evaluation with three priority layers:
    ///
    ///   1. Explicit Deny  → immediately false (deny always wins)
    ///   2. Explicit Allow  → immediately true
    ///   3. Department Default → true if the default grants the requested permission
    ///   4. No match        → false
    ///
    /// Department defaults act as a "virtual allow" — they grant baseline access
    /// without requiring any AccessEntry records. An explicit Deny on any ancestor
    /// folder will still block access even if the department default would allow it.
    /// </summary>
    private static bool EvaluateAccess(
        List<AccessEntry> effectiveAcl,
        HashSet<Guid> userPrincipalIds,
        FilePermission permission,
        FilePermission departmentDefault)
    {
        // -- Layer 1: Explicit Deny (highest priority) --
        foreach (var entry in effectiveAcl)
        {
            if (entry.EntryType != AclEntryType.Deny) continue;
            if (!userPrincipalIds.Contains(entry.PrincipalId)) continue;
            if ((entry.Permissions & permission) != 0) return false;
        }

        // -- Layer 2: Explicit Allow --
        foreach (var entry in effectiveAcl)
        {
            if (entry.EntryType != AclEntryType.Allow) continue;
            if (!userPrincipalIds.Contains(entry.PrincipalId)) continue;
            if ((entry.Permissions & permission) != 0) return true;
        }

        // -- Layer 3: Department Default (virtual allow) --
        if ((departmentDefault & permission) != 0)
            return true;

        return false;
    }

    /// <summary>
    /// Explicit-only evaluation for the sync API (no department defaults).
    /// Used by HasAccess(UserContext, FileMetadata, FilePermission) where
    /// department context is not available.
    /// </summary>
    private static bool EvaluateExplicitOnly(
        List<AccessEntry> effectiveAcl,
        HashSet<Guid> userPrincipalIds,
        FilePermission permission)
    {
        return EvaluateAccess(effectiveAcl, userPrincipalIds, permission, FilePermission.None);
    }

    // ══════════════════════════════════════════
    //  ACL resolution helpers
    // ══════════════════════════════════════════

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

    /// <summary>
    /// Collects the principal IDs for ACL matching: User, Groups, and Roles.
    /// Department IDs are NOT included here — department defaults are handled
    /// as a separate virtual layer, not as ACL principal matching.
    /// </summary>
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