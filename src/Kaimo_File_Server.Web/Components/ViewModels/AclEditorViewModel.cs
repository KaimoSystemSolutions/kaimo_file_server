using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Language;
using Microsoft.AspNetCore.Components.Authorization;
using System.Runtime.Versioning;

public class AclEditorViewModel
{
    private readonly IAclRepository _aclRepo;
    private readonly IFileMetadataRepository _metaRepo;
    private readonly IUserRepository _userRepo;
    private readonly IGroupRepository _groupRepo;
    private readonly IRoleRepository _roleRepo;
    private readonly IShareRepository _shareRepo;
    private readonly ISyncDefinitionRepository _syncDefinitions;
    private readonly IDepartmentRepository _departmentRepo;
    private readonly IDepartmentPermissionService _deptPermissions;
    private readonly IManagementAuthService _mgmtAuth;
    private readonly IUserContextFactory _userContextFactory;
    private readonly AuthenticationStateProvider _authState;
    private readonly ILogger<AclEditorViewModel> _logger;

    public AclEditorViewModel(
        IAclRepository aclRepo,
        IFileMetadataRepository metaRepo,
        IUserRepository userRepo,
        IGroupRepository groupRepo,
        IRoleRepository roleRepo,
        IShareRepository shareRepo,
        ISyncDefinitionRepository syncDefinitions,
        IDepartmentRepository departmentRepo,
        IDepartmentPermissionService deptPermissions,
        IManagementAuthService mgmtAuth,
        IUserContextFactory userContextFactory,
        AuthenticationStateProvider authState,
        ILogger<AclEditorViewModel> logger)
    {
        _aclRepo = aclRepo;
        _metaRepo = metaRepo;
        _userRepo = userRepo;
        _groupRepo = groupRepo;
        _roleRepo = roleRepo;
        _shareRepo = shareRepo;
        _syncDefinitions = syncDefinitions;
        _departmentRepo = departmentRepo;
        _deptPermissions = deptPermissions;
        _mgmtAuth = mgmtAuth;
        _userContextFactory = userContextFactory;
        _authState = authState;
        _logger = logger;
    }

    /// <summary>
    /// Server-side authorization gate for ACL management. The UI merely *hides* the
    /// editor from non-managers — this is the actual enforcement, and it is scoped:
    /// the current user must hold <see cref="ManagementPermission.ManageShareAcls"/>
    /// on THIS share (globally, via its department, or a direct share assignment).
    /// Every load and mutation goes through here so the data layer never trusts the UI.
    /// </summary>
    private async Task<bool> CanManageAclsAsync()
    {
        var state = await _authState.GetAuthenticationStateAsync();
        var username = state.User.Identity?.Name;
        if (string.IsNullOrEmpty(username)) return false;

        var actor = await _userContextFactory.CreateByUsernameAsync(username);
        if (actor is null) return false;

        return await _mgmtAuth.CanManageShareAsync(
            actor, ShareId, ManagementPermission.ManageShareAcls);
    }

    // ------------------ State ------------------

    public Guid ShareId { get; private set; }
    public Guid FileMetadataId { get; private set; }
    public string NormalizedPath { get; private set; } = "";

    public bool IsLoaded { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string? SuccessMessage { get; private set; }

    /// <summary>
    /// Non-blocking notice shown when this path lives inside a synced folder: ACLs
    /// on items there are not preserved when the synchronization renames or moves
    /// them. Null when the path is not inside a synced folder (or is the sync root
    /// itself, which is stable).
    /// </summary>
    public string? WarningMessage { get; private set; }

    /// <summary>
    /// True when the enclosing sync folder is configured for root-level-only
    /// permission management and this path is strictly below its root; ACL
    /// additions and edits are then refused. Deletions stay allowed so stale
    /// entries can be cleaned up.
    /// </summary>
    private bool _blockAclWrites;

    /// <summary>This path's own ACLs (editable)</summary>
    public List<AccessEntry> Entries { get; private set; } = [];

    /// <summary>ACLs inherited from parent paths (read-only)</summary>
    public List<InheritedAclEntry> InheritedEntries { get; private set; } = [];

    /// <summary>
    /// The share's department default permission (read-only), or null if the
    /// share's department (and its ancestors) define no default. This is a
    /// share-wide baseline that grants the listed permissions to every member
    /// of the department — it is NOT a path-inherited ACL entry and has lower
    /// precedence than any explicit Allow/Deny, so it is shown separately.
    /// </summary>
    public DepartmentDefaultInfo? DepartmentDefault { get; private set; }

    public List<User> AllUsers { get; private set; } = [];
    public List<Group> AllGroups { get; private set; } = [];

    /// <summary>
    /// Roles are intentionally NOT offered as a principal when creating ACL entries:
    /// file access is expressed through users and groups, while roles carry
    /// administrative (management) permissions — a separation the editor keeps clear.
    /// This list is still loaded so that any pre-existing role-based entries continue
    /// to resolve to a readable name and icon; <see cref="AclService"/> also still
    /// evaluates them, so no access silently changes.
    /// </summary>
    public List<Role> AllRoles { get; private set; } = [];

    // ------------------ New Entry State ------------------

    public bool IsAddingEntry { get; set; }
    public string NewPrincipalType { get; set; } = "user";
    public Guid? NewPrincipalId { get; set; }
    public AclEntryType NewEntryType { get; set; } = AclEntryType.Allow;
    public FilePermission NewPermissions { get; set; } = FilePermission.None;
    public AclInheritance NewInheritance { get; set; } = AclInheritance.Everything;

    // ------------------ Edit State ------------------

    public Guid? EditingEntryId { get; set; }

    // ------------------ Load ------------------

    public async Task LoadAsync(string path, Guid shareId, bool isDirectory = true)
    {
        IsLoaded = false;
        ErrorMessage = null;
        SuccessMessage = null;
        WarningMessage = null;
        _blockAclWrites = false;

        try
        {
            ShareId = shareId;
            NormalizedPath = ShareRelativePath.Normalize(path);

            // Authorization first — never load (and thus never disclose) ACLs for a
            // share the user is not allowed to manage. IsLoaded stays false, so the
            // editor shows an error banner instead of any permission data.
            if (!await CanManageAclsAsync())
            {
                _logger.LogWarning(
                    "Unauthorized ACL load blocked: path='{Path}', shareId={ShareId}",
                    path, shareId);
                ErrorMessage = Resources.Web_Error_AccessDenied;
                return;
            }

            _logger.LogDebug(
                "AclEditor loading: raw='{RawPath}' → normalized='{NormalizedPath}', shareId={ShareId}",
                path, NormalizedPath, shareId);

            // Flag / restrict ACL management inside a synced folder (see fields).
            await EvaluateSyncFolderPolicyAsync(shareId);

            // Load or create this path's own FileMetadata
            var meta = await _metaRepo.GetOrCreateAsync(
                NormalizedPath, isDirectory, Guid.Empty, shareId);

            FileMetadataId = meta.Id;

            // Load this path's own ACLs
            Entries = await _aclRepo.GetByFileMetadataIdAsync(meta.Id);

            // Load ACLs inherited from parent paths
            InheritedEntries = await LoadInheritedAclsAsync(shareId, isDirectory);

            // Resolve the share's department default (share-wide baseline)
            DepartmentDefault = await LoadDepartmentDefaultAsync(shareId);

            AllUsers = (await _userRepo.GetAllAsync()).ToList();
            AllGroups = (await _groupRepo.GetAllAsync()).ToList();
            AllRoles = (await _roleRepo.GetAllAsync()).ToList();

            _logger.LogDebug(
                "AclEditor loaded: metaId={MetaId}, own={OwnCount}, inherited={InhCount}, path='{Path}'",
                meta.Id, Entries.Count, InheritedEntries.Count, NormalizedPath);

            IsLoaded = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading ACL for '{Path}'", path);
            ErrorMessage = Resources.Web_Acl_LoadPermissionsFailed;
        }
    }

    /// <summary>
    /// Sets <see cref="WarningMessage"/> and the internal write-block flag based on
    /// whether <see cref="NormalizedPath"/> lies strictly below an enabled sync
    /// definition's root in this share. The root itself is stable configuration,
    /// so it is neither warned about nor blocked.
    /// </summary>
    private async Task EvaluateSyncFolderPolicyAsync(Guid shareId)
    {
        var definitions = await _syncDefinitions.GetEnabledByShareAsync(shareId);
        var enclosing = definitions.FirstOrDefault(
            def => IsStrictlyInside(def.LocalPath, NormalizedPath));
        if (enclosing is null)
            return;

        WarningMessage = Resources.Web_Acl_SyncFolderWarning;
        if (enclosing.AdvancedSettings.RootLevelPermissionsOnly)
            _blockAclWrites = true;
    }

    /// <summary>
    /// True when <paramref name="path"/> is a descendant of <paramref name="root"/>
    /// (not the root itself). Both are already <see cref="ShareRelativePath"/>-normalized
    /// (forward slashes, no surrounding slash, "" = share root).
    /// </summary>
    private static bool IsStrictlyInside(string root, string path)
    {
        if (string.Equals(root, path, StringComparison.OrdinalIgnoreCase))
            return false;
        return root.Length == 0
            || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<List<InheritedAclEntry>> LoadInheritedAclsAsync(Guid shareId, bool isDirectory)
    {
        var hierarchy = ShareRelativePath.BuildHierarchy(NormalizedPath);

        // Only parent paths, not the current path itself
        var parentPaths = hierarchy.Where(p => p != NormalizedPath).ToList();

        if (parentPaths.Count == 0)
            return [];

        var allAcls = await _aclRepo.GetAclsForPathsAsync(shareId, parentPaths);
        var result = new List<InheritedAclEntry>();
        var targetDepth = ShareRelativePath.GetDepth(NormalizedPath);

        foreach (var (sourcePath, sourceIsDir, acl) in allAcls)
        {
            if (acl == null || acl.Count == 0)
                continue;

            foreach (var entry in acl)
            {
                if (AppliesToDescendant(entry, isDirectory, sourcePath, targetDepth))
                {
                    result.Add(new InheritedAclEntry
                    {
                        Entry = entry,
                        SourcePath = sourcePath
                    });
                }
            }
        }

        return result;
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
    /// Resolves the department default that applies to this share, for display only.
    /// Mirrors the "virtual allow" layer in <see cref="AclService"/>: the value comes
    /// from the share's own department, walking up the department hierarchy for
    /// inheritance. Returns null when no department in the chain defines a default.
    /// This is deliberately membership-independent — the admin view shows the baseline
    /// that applies to department members, not to the admin currently viewing it.
    /// </summary>
    private async Task<DepartmentDefaultInfo?> LoadDepartmentDefaultAsync(Guid shareId)
    {
        var share = await _shareRepo.GetByIdAsync(shareId);
        if (share is null)
            return null;

        var info = await _deptPermissions.GetPermissionInfoAsync(share.DepartmentId);
        if (info.HasNoDefault || info.EffectivePermission == 0)
            return null;

        var dept = await _departmentRepo.GetByIdAsync(share.DepartmentId);

        return new DepartmentDefaultInfo
        {
            DepartmentName = dept?.Name ?? "",
            Permissions = (FilePermission)info.EffectivePermission,
            InheritedFromDepartmentName = info.IsOwnPermission ? null : info.InheritedFromDepartmentName
        };
    }

    // ------------------ Add/Edit/Delete bleiben gleich ------------------

    public void StartAddEntry()
    {
        IsAddingEntry = true;
        NewPrincipalType = "user";
        NewPrincipalId = null;
        NewEntryType = AclEntryType.Allow;
        NewPermissions = FilePermission.None;
        NewInheritance = AclInheritance.Everything;
        EditingEntryId = null;
        ErrorMessage = null;
        SuccessMessage = null;
    }

    public void CancelAddEntry() => IsAddingEntry = false;

    public async Task<bool> AddEntryAsync()
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (!await CanManageAclsAsync())
        { ErrorMessage = Resources.Web_Error_AccessDenied; return false; }

        if (_blockAclWrites)
        { ErrorMessage = Resources.Web_Acl_SyncFolderBlocked; return false; }

        if (NewPrincipalId is null)
        { ErrorMessage = Resources.Web_Error_PrincipalNotSelected; return false; }

        if (NewPermissions == FilePermission.None)
        { ErrorMessage = Resources.Web_Error_PermissionNotSelected; return false; }

        var duplicate = Entries.FirstOrDefault(e =>
            e.PrincipalId == NewPrincipalId.Value && e.EntryType == NewEntryType);
        if (duplicate is not null)
        {
            ErrorMessage = Resources.Web_Error_DuplicatePermissionEntry;
            return false;
        }

        try
        {
            var entry = new AccessEntry(
                NewPrincipalId.Value, NewEntryType, NewPermissions, NewInheritance)
            {
                FileMetadataId = FileMetadataId
            };

            await _aclRepo.AddAsync(entry);
            Entries = await _aclRepo.GetByFileMetadataIdAsync(FileMetadataId);

            SuccessMessage = Resources.Web_Acl_Added;
            IsAddingEntry = false;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding ACL entry");
            ErrorMessage = Resources.Web_Error_AddFailed;
            return false;
        }
    }

    public void StartEditEntry(AccessEntry entry)
    {
        EditingEntryId = entry.Id;
        NewPrincipalId = entry.PrincipalId;
        NewEntryType = entry.EntryType;
        NewPermissions = entry.Permissions;
        NewInheritance = entry.Inheritance;
        IsAddingEntry = false;
        ErrorMessage = null;
        SuccessMessage = null;
        NewPrincipalType = ResolvePrincipalType(entry.PrincipalId);
    }

    public void CancelEditEntry() => EditingEntryId = null;

    public async Task<bool> SaveEditEntryAsync()
    {
        if (EditingEntryId is null) return false;
        ErrorMessage = null;
        SuccessMessage = null;

        if (!await CanManageAclsAsync())
        { ErrorMessage = Resources.Web_Error_AccessDenied; return false; }

        if (_blockAclWrites)
        { ErrorMessage = Resources.Web_Acl_SyncFolderBlocked; return false; }

        if (NewPermissions == FilePermission.None)
        { ErrorMessage = Resources.Web_Acl_SelectAtLeastOnePermission; return false; }

        try
        {
            var entry = Entries.FirstOrDefault(e => e.Id == EditingEntryId.Value);
            if (entry is null)
            { ErrorMessage = Resources.Web_Error_EntryNotFound; return false; }

            entry.EntryType = NewEntryType;
            entry.Permissions = NewPermissions;
            entry.Inheritance = NewInheritance;

            await _aclRepo.UpdateAsync(entry);
            Entries = await _aclRepo.GetByFileMetadataIdAsync(FileMetadataId);

            SuccessMessage = Resources.Web_Acl_Updated;
            EditingEntryId = null;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating ACL entry");
            ErrorMessage = Resources.Web_Error_SaveFailed;
            return false;
        }
    }

    public async Task<bool> DeleteEntryAsync(Guid entryId)
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (!await CanManageAclsAsync())
        { ErrorMessage = Resources.Web_Error_AccessDenied; return false; }

        try
        {
            await _aclRepo.DeleteAsync(entryId);
            Entries = await _aclRepo.GetByFileMetadataIdAsync(FileMetadataId);
            SuccessMessage = Resources.Web_Acl_Removed;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting ACL entry");
            ErrorMessage = Resources.Web_Error_RemoveFailed;
            return false;
        }
    }

    // ------------------ Display Helpers ------------------

    public string GetPrincipalDisplayName(Guid principalId)
    {
        var user = AllUsers.FirstOrDefault(u => u.Id == principalId);
        if (user is not null) return user.Name ?? user.Username;

        var group = AllGroups.FirstOrDefault(g => g.Id == principalId);
        if (group is not null) return group.Name;

        var role = AllRoles.FirstOrDefault(r => r.Id == principalId);
        if (role is not null) return role.Name;

        return principalId.ToString()[..8] + "…";
    }

    public string GetPrincipalType(Guid principalId) => ResolvePrincipalType(principalId);

    public string GetPrincipalIcon(Guid principalId) => ResolvePrincipalType(principalId) switch
    {
        "user" => "user",
        "group" => "group",
        "role" => "role",
        _ => "unknown"
    };

    private string ResolvePrincipalType(Guid principalId)
    {
        if (AllUsers.Any(u => u.Id == principalId)) return "user";
        if (AllGroups.Any(g => g.Id == principalId)) return "group";
        if (AllRoles.Any(r => r.Id == principalId)) return "role";
        return "unknown";
    }

    // ------------------ Permission Helpers ------------------

    public bool HasPermission(FilePermission flags, FilePermission flag)
        => (flags & flag) != 0;

    public FilePermission TogglePermission(FilePermission flags, FilePermission flag)
        => flags ^ flag;

    public FilePermission ApplyShortcut(FilePermission flags, FilePermission shortcut)
    {
        if ((flags & shortcut) == shortcut)
            return flags & ~shortcut;
        return flags | shortcut;
    }

    public bool IsShortcutFullySet(FilePermission flags, FilePermission shortcut)
        => (flags & shortcut) == shortcut;
}

/// <summary>
/// Wrapper for inherited ACL entries, carrying the source path they came from.
/// </summary>
public class InheritedAclEntry
{
    public AccessEntry Entry { get; set; } = null!;
    public string SourcePath { get; set; } = "";
}

/// <summary>
/// The share's department default permission, resolved for read-only display.
/// Applies share-wide to all members of the department, distinct from the
/// per-principal, path-based ACL entries.
/// </summary>
public class DepartmentDefaultInfo
{
    /// <summary>The department that owns the share.</summary>
    public string DepartmentName { get; init; } = "";

    /// <summary>The effective baseline permissions granted to department members.</summary>
    public FilePermission Permissions { get; init; }

    /// <summary>
    /// Name of the ancestor department the default is inherited from, or null when
    /// the owning department defines the default itself.
    /// </summary>
    public string? InheritedFromDepartmentName { get; init; }
}