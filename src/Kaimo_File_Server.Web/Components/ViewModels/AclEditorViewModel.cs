using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Language;
using System.Runtime.Versioning;

public class AclEditorViewModel
{
    private readonly IAclRepository _aclRepo;
    private readonly IFileMetadataRepository _metaRepo;
    private readonly IUserRepository _userRepo;
    private readonly IGroupRepository _groupRepo;
    private readonly IRoleRepository _roleRepo;
    private readonly ILogger<AclEditorViewModel> _logger;

    public AclEditorViewModel(
        IAclRepository aclRepo,
        IFileMetadataRepository metaRepo,
        IUserRepository userRepo,
        IGroupRepository groupRepo,
        IRoleRepository roleRepo,
        ILogger<AclEditorViewModel> logger)
    {
        _aclRepo = aclRepo;
        _metaRepo = metaRepo;
        _userRepo = userRepo;
        _groupRepo = groupRepo;
        _roleRepo = roleRepo;
        _logger = logger;
    }

    // ------------------ State ------------------

    public Guid ShareId { get; private set; }
    public Guid FileMetadataId { get; private set; }
    public string NormalizedPath { get; private set; } = "";

    public bool IsLoaded { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string? SuccessMessage { get; private set; }

    /// <summary>This path's own ACLs (editable)</summary>
    public List<AccessEntry> Entries { get; private set; } = [];

    /// <summary>ACLs inherited from parent paths (read-only)</summary>
    public List<InheritedAclEntry> InheritedEntries { get; private set; } = [];

    public List<User> AllUsers { get; private set; } = [];
    public List<Group> AllGroups { get; private set; } = [];
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

        try
        {
            ShareId = shareId;
            NormalizedPath = ShareRelativePath.Normalize(path);

            _logger.LogDebug(
                "AclEditor loading: raw='{RawPath}' → normalized='{NormalizedPath}', shareId={ShareId}",
                path, NormalizedPath, shareId);

            // Load or create this path's own FileMetadata
            var meta = await _metaRepo.GetOrCreateAsync(
                NormalizedPath, isDirectory, Guid.Empty, shareId);

            FileMetadataId = meta.Id;

            // Load this path's own ACLs
            Entries = await _aclRepo.GetByFileMetadataIdAsync(meta.Id);

            // Load ACLs inherited from parent paths
            InheritedEntries = await LoadInheritedAclsAsync(shareId, isDirectory);

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