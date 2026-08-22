using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Search;
using Microsoft.AspNetCore.Components.Authorization;
using System.Security.Claims;
using System.Text.RegularExpressions;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public partial class ShareListViewModel
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private readonly IShareRepository _shareRepo;
    private readonly IUserRepository _userRepo;
    private readonly IGroupRepository _groupRepo;
    private readonly IAclRepository _aclRepo;
    private readonly IFileMetadataRepository _metaRepo;
    private readonly IAclService _aclService;
    private readonly IManagementAuthService _mgmtAuth;
    private readonly IUserContextFactory _userContextFactory;
    private readonly ShareLockManager _lockManager;
    private readonly AuthenticationStateProvider _authState;
    private readonly ILogger<ShareListViewModel> _logger;
    private readonly IReadOnlyList<string> _storagePools;
    private readonly IFileVersionService? _versionService;
    private readonly ISearchService? _searchService;
    private readonly ICloudSyncOperationCoordinator? _cloudSyncOperations;

    public ShareListViewModel(
        IShareRepository shareRepo,
        IUserRepository userRepo,
        IGroupRepository groupRepo,
        IAclRepository aclRepo,
        IFileMetadataRepository metaRepo,
        IAclService aclService,
        IManagementAuthService mgmtAuth,
        IUserContextFactory userContextFactory,
        ShareLockManager lockManager,
        AuthenticationStateProvider authState,
        ILogger<ShareListViewModel> logger,
        IReadOnlyList<string> storagePools,
        IFileVersionService? versionService = null,
        ISearchService? searchService = null,
        ICloudSyncOperationCoordinator? cloudSyncOperations = null)
    {
        _shareRepo = shareRepo;
        _userRepo = userRepo;
        _groupRepo = groupRepo;
        _aclRepo = aclRepo;
        _metaRepo = metaRepo;
        _aclService = aclService;
        _mgmtAuth = mgmtAuth;
        _userContextFactory = userContextFactory;
        _lockManager = lockManager;
        _authState = authState;
        _logger = logger;
        _storagePools = storagePools;
        StoragePools = storagePools
            .Select(path => new StoragePoolItem(GetPoolDisplayName(path), path))
            .ToList();
        NewSharePoolPath = storagePools.FirstOrDefault() ?? string.Empty;
        _versionService = versionService;
        _searchService = searchService;
        _cloudSyncOperations = cloudSyncOperations;
    }

    // -- State --

    public List<ShareDefinition> Shares { get; private set; } = [];
    public bool IsLoading { get; private set; }
    public string? ErrorMessage { get; private set; }

    // -- Create --

    public bool IsCreating { get; set; }
    public string NewShareName { get; set; } = "";
    public string NewSharePoolPath { get; set; }
    public string? CreateErrorMessage { get; private set; }

    // -- Edit --

    public ShareDefinition? SelectedShare { get; private set; }
    public string EditShareName { get; set; } = "";
    public string EditSharePoolPath { get; set; } = "";
    public string? EditErrorMessage { get; private set; }
    public string? EditSuccessMessage { get; private set; }
    public bool ShowDeleteConfirm { get; set; }
    public bool IsRenaming { get; private set; }
    public bool IsMovingPool { get; private set; }

    public IReadOnlyList<StoragePoolItem> StoragePools { get; }

    // -- Access --

    public bool ShowAccessPanel { get; set; }
    public List<User> AllUsers { get; private set; } = [];
    public List<Core.Domain.Identity.Group> AllGroups { get; private set; } = [];
    public string? AccessErrorMessage { get; private set; }

    // -- Computed --

    public string CurrentUserName { get; private set; } = "";

    /// <summary>True if the actor may manage at least one share (any scope).</summary>
    public bool IsAdmin { get; private set; }
    public bool CanCreateShare { get; private set; }

    // Management scope for the current actor (resolved in LoadAsync).
    // _manageAllShares == true  → unrestricted (Global) admin.
    // otherwise _manageableShareIds holds the in-scope share IDs.
    private bool _manageAllShares;
    private HashSet<Guid> _manageableShareIds = new();

    // The resolved actor, kept so per-action mutations can re-check the SPECIFIC
    // management permission (not just "manages some share") server-side.
    private UserContext? _actorContext;

    /// <summary>
    /// Whether the current actor may open the management panel for a specific share.
    /// A share the actor only sees via ACL (normal user view) is NOT manageable.
    /// This is the coarse "any share-management right" gate used for listing/visibility;
    /// individual mutations additionally re-check their specific permission via
    /// <see cref="CanManageSelectedShareAsync"/>.
    /// </summary>
    public bool CanManageShare(Guid shareId)
        => _manageAllShares || _manageableShareIds.Contains(shareId);

    /// <summary>
    /// Server-side per-action authorization: does the actor hold <paramref name="required"/>
    /// on the currently selected share (globally, via its department, or a direct share
    /// assignment)? The razor hides controls the actor cannot use; this is the enforcement.
    /// </summary>
    private Task<bool> CanManageSelectedShareAsync(ManagementPermission required)
        => SelectedShare is not null && _actorContext is not null
            ? _mgmtAuth.CanManageShareAsync(_actorContext, SelectedShare.Id, required)
            : Task.FromResult(false);

    [GeneratedRegex(@"^[a-zA-Z0-9\-_.]+$")]
    private static partial Regex SafeShareNameRegex();

    // -- Load --

    public async Task LoadAsync()
    {
        try
        {
            IsLoading = true;
            ErrorMessage = null;

            var state = await _authState.GetAuthenticationStateAsync();
            CurrentUserName = state.User.FindFirst("display_name")?.Value
                           ?? state.User.Identity?.Name
                           ?? "";

            var username = state.User.Identity?.Name;
            var actor = string.IsNullOrEmpty(username)
                ? null
                : await _userContextFactory.CreateByUsernameAsync(username);

            if (actor is null)
            {
                // No resolvable user → show nothing (default deny).
                _actorContext = null;
                IsAdmin = false;
                CanCreateShare = false;
                _manageAllShares = false;
                _manageableShareIds = new();
                Shares = [];
                return;
            }

            _actorContext = actor;

            // -- Resolve management scope (any share-management right) --
            // Global scope → unrestricted; otherwise limited to the actor's
            // department(s) + descendants. These shares are shown to the manager
            // regardless of ACL, hidden, or enabled state.
            var mgmtScope = await _mgmtAuth.GetAuthorizedShareIdsAnyAsync(
                actor, ManagementPermission.ShareAdmin);

            _manageAllShares = mgmtScope.IsUnrestricted;
            _manageableShareIds = mgmtScope.IsUnrestricted
                ? new HashSet<Guid>()
                : mgmtScope.ScopeIds.ToHashSet();

            IsAdmin = _manageAllShares || _manageableShareIds.Count > 0;
            CanCreateShare = await _mgmtAuth.HasAnyPermissionAsync(
                actor, ManagementPermission.CreateShares);

            // Load every share, then filter per actor.
            var allShares = await _shareRepo.GetAllAsync();
            var visible = new List<ShareDefinition>(allShares.Count);

            foreach (var share in allShares)
            {
                // (A) Management view: in scope → always visible (incl. hidden/disabled).
                if (_manageAllShares || _manageableShareIds.Contains(share.Id))
                {
                    visible.Add(share);
                    continue;
                }

                // (B) Normal user view: must be enabled, not hidden, and the actor
                //     needs ListReadData on the share root.
                if (!share.IsEnabled || share.IsShareHidden)
                    continue;

                if (await _aclService.HasAccessAsync(
                        actor, share.Id, "", true, FilePermission.ListReadData))
                    visible.Add(share);
            }

            Shares = visible;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading the shares");
            ErrorMessage = Resources.Web_Error_LoadSharesFailed;
            Shares = [];
        }
        finally
        {
            IsLoading = false;
        }
    }

    // -- Validation --

    private bool ValidateShareName(string name, out string? error)
    {
        error = null;

        if (string.IsNullOrEmpty(name))
        { error = Resources.Web_Validation_NameEmpty; return false; }

        if (name.Length > SambaName.MaxShareNameBytes)
        { error = Resources.Web_ShareName_MaxLength; return false; }

        if (!SafeShareNameRegex().IsMatch(name))
        { error = Resources.Web_ShareName_AllowedChars; return false; }

        if (name.StartsWith('.') || name.EndsWith('.'))
        { error = Resources.Web_ShareName_NoLeadingTrailingDot; return false; }

        if (!SambaName.IsValidShareName(name))
        {
            error = string.Format(Resources.Web_Validation_ReservedName, name);
            return false;
        }

        return true;
    }

    private string? GetConfiguredPoolPath(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return null;

        var normalized = Path.GetFullPath(candidate);
        return _storagePools
            .FirstOrDefault(path =>
                PathComparer.Equals(Path.GetFullPath(path), normalized));
    }

    private static string BuildSharePath(string poolPath, string name)
        => Path.Combine(poolPath, name);

    private static string GetPoolDisplayName(string poolPath)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(poolPath));
        return Path.GetFileName(normalized);
    }

    public string? GetPoolNameForShare(ShareDefinition share)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(share.Path));
        if (parent is null) return null;

        return StoragePools.FirstOrDefault(pool =>
            PathComparer.Equals(Path.GetFullPath(pool.Path), parent))?.Name;
    }

    public string GetStoragePoolDisplayNameForShare(ShareDefinition share)
    {
        try
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(share.Path));
            if (parent is null)
                return "—";

            var configuredName = StoragePools.FirstOrDefault(pool =>
                PathComparer.Equals(Path.GetFullPath(pool.Path), parent))?.Name;

            var parentName = Path.GetFileName(
                Path.TrimEndingDirectorySeparator(parent));
            return configuredName
                   ?? (!string.IsNullOrWhiteSpace(parentName) ? parentName : parent);
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException)
        {
            _logger.LogWarning(ex,
                "Could not determine the storage pool for share '{ShareName}'",
                share.Name);
            return "—";
        }
    }

    /// <summary>
    /// A share is writable from the management UI only while its persisted path
    /// belongs directly to one of the storage pools available to this process.
    /// This intentionally uses the currently injected pool list; it can later be
    /// switched to VolumeMountManager without changing the UI contract.
    /// </summary>
    public bool IsShareStorageAvailable(ShareDefinition? share)
    {
        if (share is null || string.IsNullOrWhiteSpace(share.Path))
            return false;

        try
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(share.Path));
            return parent is not null && _storagePools.Any(pool =>
                PathComparer.Equals(Path.GetFullPath(pool), parent));
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException)
        {
            _logger.LogWarning(ex,
                "Share '{ShareName}' has an invalid storage path and is read-only",
                share.Name);
            return false;
        }
    }

    public bool IsSelectedShareReadOnly
        => SelectedShare is not null && !IsShareStorageAvailable(SelectedShare);

    private bool EnsureSelectedShareWritable()
    {
        if (!IsSelectedShareReadOnly)
            return true;

        EditErrorMessage = Resources.Web_Error_ShareStorageUnavailableReadOnly;
        return false;
    }

    // -- Create --

    public async Task<bool> CreateShareAsync()
    {
        CreateErrorMessage = null;
        var name = NewShareName.Trim();

        if (!ValidateShareName(name, out var error))
        { CreateErrorMessage = error; return false; }

        try
        {
            var poolPath = GetConfiguredPoolPath(NewSharePoolPath);
            if (poolPath is null)
            {
                CreateErrorMessage = Resources.Web_Error_StoragePoolRequired;
                return false;
            }

            var existing = await _shareRepo.GetByNameAsync(name);
            if (existing is not null)
            { CreateErrorMessage = Resources.Web_Error_ShareExists; return false; }

            // Den Ersteller ZUERST auflösen. Ohne gültigen Owner darf kein Share
            // entstehen, sonst bleibt ein verwaister Share ohne Owner-ACL zurück.
            // Identity?.Name-Guard vermeidet den bisherigen Null-Deref.
            var state = await _authState.GetAuthenticationStateAsync();
            var username = state.User.Identity?.Name;
            var user = string.IsNullOrEmpty(username)
                ? null
                : await _userRepo.GetByUsernameAsync(username);

            if (user is null)
            {
                _logger.LogWarning(
                    "Share creation aborted: could not resolve creator (user='{User}')", username);
                CreateErrorMessage = Resources.Web_Error_CreateShareFailed;
                return false;
            }

            var sharePath = BuildSharePath(poolPath, name);
            if (Directory.Exists(sharePath) || File.Exists(sharePath))
            {
                CreateErrorMessage = Resources.Web_Error_StoragePoolDestinationExists;
                return false;
            }

            var share = new ShareDefinition(name, sharePath);
            Directory.CreateDirectory(share.Path);
            await _shareRepo.CreateAsync(share);

            // Root-FileMetadata persistieren (Path == "", Konvention aus
            // ShareRelativePath) und die Owner-FullControl-ACL daran hängen.
            // GetOrCreateAsync schreibt die Metadata-Zeile tatsächlich in die DB —
            // vorher wurde nur die ACL angelegt, deren FileMetadata nie existierte.
            var rootMeta = await _metaRepo.GetOrCreateAsync(
                "", isDirectory: true, userId: user.Id, shareId: share.Id);

            // Everything (ThisFolder | SubFolders | SubFiles | AllDescendants) — die
            // Owner-Regel MUSS auch für den Share-Root selbst gelten. Mit nur
            // AllDescendants greift sie ausschließlich für Unterelemente, wodurch der
            // Root-Listing-Check (FileService.ListAsync) selbst für den Ersteller
            // fehlschlägt und der Filebrowser "Zugriff verweigert" zeigt.
            var newAcl = new AccessEntry(
                user.Id,
                AclEntryType.Allow,
                FilePermission.FullControl,
                AclInheritance.Everything)
            {
                FileMetadataId = rootMeta.Id
            };

            await _aclRepo.AddAsync(newAcl);

            var adminAcl = new AccessEntry(
                WellKnownGUIDs.ROLE_ADMIN,
                AclEntryType.Allow,
                FilePermission.FullControl,
                AclInheritance.Everything)
            {
                FileMetadataId = rootMeta.Id
            };

            await _aclRepo.AddAsync(adminAcl);

            _logger.LogInformation("Share '{ShareName}' created", name);

            NewShareName = "";
            NewSharePoolPath = _storagePools.FirstOrDefault() ?? string.Empty;
            IsCreating = false;
            await LoadAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating share '{ShareName}'", name);
            CreateErrorMessage = Resources.Web_Error_CreateShareFailed;
            return false;
        }
    }

    // -- Select / Deselect --

    public void SelectShare(ShareDefinition share)
    {
        if (SelectedShare?.Id == share.Id)
        {
            DeselectShare();
            return;
        }

        SelectedShare = share;
        EditShareName = share.Name;
        EditSharePoolPath = Path.GetDirectoryName(Path.GetFullPath(share.Path))
            ?? _storagePools.FirstOrDefault()
            ?? string.Empty;
        EditErrorMessage = null;
        EditSuccessMessage = null;
        ShowDeleteConfirm = false;
        ShowAccessPanel = false;
        IsCreating = false;
    }

    public void DeselectShare()
    {
        SelectedShare = null;
        EditShareName = "";
        EditSharePoolPath = "";
        EditErrorMessage = null;
        EditSuccessMessage = null;
        ShowDeleteConfirm = false;
        ShowAccessPanel = false;
    }

    // -- Rename (mit Lock) --

    public async Task<bool> RenameShareAsync()
    {
        if (SelectedShare is null) return false;

        EditErrorMessage = null;
        EditSuccessMessage = null;

        if (!EnsureSelectedShareWritable())
            return false;

        // Server-side authorization guard (see DeleteShareAsync).
        if (!await CanManageSelectedShareAsync(ManagementPermission.EditShareSettings))
        { EditErrorMessage = Resources.Web_Error_NoPermission; return false; }

        var newName = EditShareName.Trim();
        var oldName = SelectedShare.Name;

        if (newName == oldName)
        { EditErrorMessage = Resources.Web_Rename_Unchanged; return false; }

        if (!ValidateShareName(newName, out var error))
        { EditErrorMessage = error; return false; }

        var existingNew = await _shareRepo.GetByNameAsync(newName);
        if (existingNew is not null)
        { EditErrorMessage = Resources.Web_Error_ShareExists; return false; }

        var shareLock = _lockManager.GetLock(oldName);

        IsRenaming = true;
        try
        {
            // Lock holen – wartet bis alle laufenden Ops fertig sind
            if (!await shareLock.WaitAsync(TimeSpan.FromSeconds(30)))
            {
                EditErrorMessage = Resources.Web_Error_ShareInUse;
                return false;
            }

            try
            {
                var cloudOperation = await TryBeginCloudSafeShareMutationAsync(
                    SelectedShare.Id);
                if (!cloudOperation.Acquired)
                {
                    EditErrorMessage = Resources.Web_Error_ShareInUse;
                    return false;
                }
                await using var cloudOperationLease = cloudOperation.Lease;
                // The persisted share path is the source of truth. Keep the
                // current pool and only replace the final directory component.
                var oldFullPath = Path.GetFullPath(SelectedShare.Path);
                var parentPath = Path.GetDirectoryName(oldFullPath)
                    ?? throw new InvalidOperationException("Share path has no parent directory.");
                var newFullPath = Path.Combine(parentPath, newName);

                if (!PathComparer.Equals(oldFullPath, newFullPath)
                    && (Directory.Exists(newFullPath) || File.Exists(newFullPath)))
                    throw new IOException($"Destination '{newFullPath}' already exists.");

                var directoryMoved = Directory.Exists(oldFullPath)
                    && !PathComparer.Equals(oldFullPath, newFullPath);
                if (directoryMoved)
                    Directory.Move(oldFullPath, newFullPath);

                // 2. ShareDefinition in DB updaten. If persistence fails, put
                // the directory back so the old persisted path remains valid.
                try
                {
                    await _shareRepo.UpdateLocationAsync(
                        SelectedShare.Id, newName, newFullPath);
                    SelectedShare.Name = newName;
                    SelectedShare.Path = newFullPath;
                }
                catch
                {
                    SelectedShare.Name = oldName;
                    SelectedShare.Path = oldFullPath;
                    if (directoryMoved && Directory.Exists(newFullPath))
                        Directory.Move(newFullPath, oldFullPath);
                    throw;
                }

                if (_searchService is not null
                    && !PathComparer.Equals(oldFullPath, newFullPath))
                {
                    try
                    {
                        await _searchService.onDirectoryRenamed(oldFullPath, newFullPath);
                    }
                    catch (Exception searchEx)
                    {
                        _logger.LogWarning(searchEx,
                            "Share '{ShareName}' renamed, but its search index could not be updated",
                            newName);
                    }
                }

                // 4. Lock-Key umbenennen
                _lockManager.RenameLock(oldName, newName);

                _logger.LogInformation("Share '{OldName}' renamed to '{NewName}'", oldName, newName);

                EditSuccessMessage = string.Format(Resources.Web_Share_RenamedTo, newName);
                EditShareName = newName;
                await LoadAsync();

                // Re-select mit neuen Daten
                var updated = Shares.FirstOrDefault(s => s.Id == SelectedShare.Id);
                if (updated is not null)
                {
                    SelectedShare = updated;
                    EditSharePoolPath = Path.GetDirectoryName(
                        Path.GetFullPath(updated.Path)) ?? EditSharePoolPath;
                }

                return true;
            }
            finally
            {
                shareLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error renaming share '{OldName}' -> '{NewName}'", oldName, newName);
            EditErrorMessage = Resources.Web_Error_RenameFailedCheckLogs;
            return false;
        }
        finally
        {
            IsRenaming = false;
        }
    }

    // -- Change storage pool --

    public async Task<bool> ChangeStoragePoolAsync()
    {
        if (SelectedShare is null) return false;

        EditErrorMessage = null;
        EditSuccessMessage = null;

        if (!EnsureSelectedShareWritable())
            return false;

        if (!await CanManageSelectedShareAsync(ManagementPermission.EditShareSettings))
        {
            EditErrorMessage = Resources.Web_Error_NoPermission;
            return false;
        }

        var targetPoolPath = GetConfiguredPoolPath(EditSharePoolPath);
        if (targetPoolPath is null)
        {
            EditErrorMessage = Resources.Web_Error_StoragePoolRequired;
            return false;
        }

        var sourcePath = Path.GetFullPath(SelectedShare.Path);
        var destinationPath = BuildSharePath(targetPoolPath, SelectedShare.Name);
        var sourceParent = Path.GetDirectoryName(sourcePath);

        if (sourceParent is not null
            && PathComparer.Equals(
                Path.GetFullPath(sourceParent), Path.GetFullPath(targetPoolPath)))
        {
            EditSuccessMessage = Resources.Web_Share_StoragePoolUnchanged;
            return true;
        }

        if (!Directory.Exists(sourcePath))
        {
            EditErrorMessage = Resources.Web_Error_SharePathMissing;
            return false;
        }

        if (Directory.Exists(destinationPath) || File.Exists(destinationPath))
        {
            EditErrorMessage = Resources.Web_Error_StoragePoolDestinationExists;
            return false;
        }

        var shareLock = _lockManager.GetLock(SelectedShare.Name);
        IsMovingPool = true;
        try
        {
            if (!await shareLock.WaitAsync(TimeSpan.FromSeconds(30)))
            {
                EditErrorMessage = Resources.Web_Error_ShareInUse;
                return false;
            }

            try
            {
                var cloudOperation = await TryBeginCloudSafeShareMutationAsync(
                    SelectedShare.Id);
                if (!cloudOperation.Acquired)
                {
                    EditErrorMessage = Resources.Web_Error_ShareInUse;
                    return false;
                }
                await using var cloudOperationLease = cloudOperation.Lease;
                var sourceWasCopied = await MoveDirectoryAcrossPoolsAsync(
                    sourcePath, destinationPath);

                try
                {
                    await _shareRepo.UpdateLocationAsync(
                        SelectedShare.Id, SelectedShare.Name, destinationPath);
                    SelectedShare.Path = destinationPath;
                }
                catch
                {
                    // Keep the persisted path usable when the DB update fails.
                    SelectedShare.Path = sourcePath;
                    if (sourceWasCopied)
                        Directory.Delete(destinationPath, recursive: true);
                    else
                        Directory.Move(destinationPath, sourcePath);
                    throw;
                }

                if (sourceWasCopied)
                {
                    try
                    {
                        Directory.Delete(sourcePath, recursive: true);
                    }
                    catch (Exception cleanupEx)
                    {
                        // The DB already points at the complete target copy. A
                        // stale source copy is safe and can be removed manually.
                        _logger.LogWarning(cleanupEx,
                            "Share '{ShareName}' moved successfully, but old path '{SourcePath}' could not be removed",
                            SelectedShare.Name, sourcePath);
                    }
                }

                if (_searchService is not null)
                {
                    try
                    {
                        await _searchService.onDirectoryRenamed(
                            sourcePath, destinationPath);
                    }
                    catch (Exception searchEx)
                    {
                        _logger.LogWarning(searchEx,
                            "Share '{ShareName}' moved, but its search index could not be updated",
                            SelectedShare.Name);
                    }
                }

                _logger.LogInformation(
                    "Share '{ShareName}' moved from '{SourcePath}' to storage pool '{PoolPath}'",
                    SelectedShare.Name, sourcePath, targetPoolPath);

                EditSuccessMessage = string.Format(
                    Resources.Web_Share_StoragePoolChanged,
                    StoragePools.First(p => PathComparer.Equals(
                        Path.GetFullPath(p.Path), Path.GetFullPath(targetPoolPath))).Name);

                await LoadAsync();
                var updated = Shares.FirstOrDefault(s => s.Id == SelectedShare.Id);
                if (updated is not null)
                {
                    SelectedShare = updated;
                    EditSharePoolPath = targetPoolPath;
                }

                return true;
            }
            finally
            {
                shareLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error moving share '{ShareName}' from '{SourcePath}' to '{DestinationPath}'",
                SelectedShare.Name, sourcePath, destinationPath);
            EditErrorMessage = Resources.Web_Error_StoragePoolMoveFailed;
            return false;
        }
        finally
        {
            IsMovingPool = false;
        }
    }

    /// <returns>
    /// <c>true</c> when a cross-filesystem copy was required and the source still
    /// exists; <c>false</c> when an atomic directory rename moved the source.
    /// </returns>
    private static async Task<bool> MoveDirectoryAcrossPoolsAsync(
        string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("Destination has no parent directory."));

        try
        {
            Directory.Move(sourcePath, destinationPath);
            return false;
        }
        catch (IOException)
        {
            // A rename cannot cross filesystem/mount boundaries. Copy into a
            // private staging directory first, publish it atomically in the
            // target pool, then remove the old share only after the copy exists.
        }

        var stagingPath = destinationPath + ".kaimo-moving-" + Guid.NewGuid().ToString("N");
        try
        {
            await CopyDirectoryAsync(sourcePath, stagingPath);
            Directory.Move(stagingPath, destinationPath);
            return true;
        }
        catch
        {
            if (Directory.Exists(stagingPath))
                Directory.Delete(stagingPath, recursive: true);
            throw;
        }
    }

    private static async Task CopyDirectoryAsync(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(destinationPath);

        foreach (var entry in new DirectoryInfo(sourcePath).EnumerateFileSystemInfos())
        {
            var destinationEntry = Path.Combine(destinationPath, entry.Name);

            // Preserve links as links. Following a link could copy data outside
            // the share or recurse forever through a directory cycle.
            if (entry.LinkTarget is not null)
            {
                if (entry is DirectoryInfo)
                    Directory.CreateSymbolicLink(destinationEntry, entry.LinkTarget);
                else
                    File.CreateSymbolicLink(destinationEntry, entry.LinkTarget);
                continue;
            }

            if (entry is DirectoryInfo directory)
            {
                await CopyDirectoryAsync(directory.FullName, destinationEntry);
                continue;
            }

            await using var source = new FileStream(
                entry.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 1024 * 1024, useAsync: true);
            await using var destination = new FileStream(
                destinationEntry, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 1024 * 1024, useAsync: true);
            await source.CopyToAsync(destination);
            File.SetLastWriteTimeUtc(
                destinationEntry, File.GetLastWriteTimeUtc(entry.FullName));
        }

        Directory.SetLastWriteTimeUtc(
            destinationPath, Directory.GetLastWriteTimeUtc(sourcePath));
    }


    // -- Toggle Share Enabled and Recycle Enabled and Share Hidden --

    public async Task<bool> ToggleShareEnabledAsync()
    {
        if (SelectedShare is null) return false;

        EditErrorMessage = null;
        EditSuccessMessage = null;

        if (!EnsureSelectedShareWritable())
            return false;

        // Enabling/disabling a share governs whether anyone can access it → ManageShareAccess.
        if (!await CanManageSelectedShareAsync(ManagementPermission.ManageShareAccess))
        { EditErrorMessage = Resources.Web_Error_NoPermission; return false; }

        try
        {
            SelectedShare.IsEnabled = !SelectedShare.IsEnabled;
            await _shareRepo.UpdateAsync(SelectedShare);

            _logger.LogInformation("Share '{ShareName}' {Status}",
                SelectedShare.Name, SelectedShare.IsEnabled ? "enabled" : "disabled");
            EditSuccessMessage = SelectedShare.IsEnabled
                ? Resources.Web_Share_Enabled
                : Resources.Web_Share_Disabled;

            await LoadAsync();

            // Re-select
            var updated = Shares.FirstOrDefault(s => s.Id == SelectedShare.Id);
            if (updated is not null)
                SelectedShare = updated;

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error changing the share status");
            EditErrorMessage = Resources.Web_Error_ChangeStatusFailed;
            return false;
        }
    }

    public async Task<bool> ToggleRecycleEnabledAsync()
    {
        if (SelectedShare is null) return false;

        EditErrorMessage = null;
        EditSuccessMessage = null;

        if (!EnsureSelectedShareWritable())
            return false;

        // The recycle bin is a share feature setting → EditShareSettings.
        if (!await CanManageSelectedShareAsync(ManagementPermission.EditShareSettings))
        { EditErrorMessage = Resources.Web_Error_NoPermission; return false; }

        try
        {
            SelectedShare.IsRecycleEnabled = !SelectedShare.IsRecycleEnabled;
            await _shareRepo.UpdateAsync(SelectedShare);

            _logger.LogInformation("Recycle bin for share '{ShareName}' {Status}",
                SelectedShare.Name, SelectedShare.IsRecycleEnabled ? "enabled" : "disabled");
            EditSuccessMessage = SelectedShare.IsRecycleEnabled
                ? Resources.Web_Share_RecycleEnabled
                : Resources.Web_Share_RecycleDisabled;

            await LoadAsync();

            // Re-select
            var updated = Shares.FirstOrDefault(s => s.Id == SelectedShare.Id);
            if (updated is not null)
                SelectedShare = updated;

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error changing the recycle bin status");
            EditErrorMessage = Resources.Web_Error_ChangeStatusFailed;
            return false;
        }
    }

    public async Task<bool> ToggleShareHiddenAsync()
    {
        if (SelectedShare is null) return false;

        EditErrorMessage = null;
        EditSuccessMessage = null;

        if (!EnsureSelectedShareWritable())
            return false;

        // Visibility (hidden = excluded from listings/ABE) is share-access control → ManageShareAccess.
        if (!await CanManageSelectedShareAsync(ManagementPermission.ManageShareAccess))
        { EditErrorMessage = Resources.Web_Error_NoPermission; return false; }

        try
        {
            SelectedShare.IsShareHidden = !SelectedShare.IsShareHidden;
            await _shareRepo.UpdateAsync(SelectedShare);

            _logger.LogInformation("Share '{ShareName}' visibility {Status}",
                SelectedShare.Name, SelectedShare.IsShareHidden ? "hidden" : "visible");
            EditSuccessMessage = SelectedShare.IsShareHidden
                ? Resources.Web_Share_Enabled
                : Resources.Web_Share_Disabled;

            await LoadAsync();

            // Re-select
            var updated = Shares.FirstOrDefault(s => s.Id == SelectedShare.Id);
            if (updated is not null)
                SelectedShare = updated;

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error changing the share visibility");
            EditErrorMessage = Resources.Web_Error_ChangeVisibilityFailed;
            return false;
        }
    }

    // -- Delete --

    public async Task<bool> DeleteShareAsync()
    {
        if (SelectedShare is null) return false;

        EditErrorMessage = null;

        if (!EnsureSelectedShareWritable())
            return false;

        // Server-side authorization: the razor hides the button when the actor
        // cannot manage this share, but the view-model method must guard too —
        // it enforces the specific DeleteShares right AND the department scope.
        if (!await CanManageSelectedShareAsync(ManagementPermission.DeleteShares))
        { EditErrorMessage = Resources.Web_Error_NoPermission; return false; }

        try
        {
            var cloudOperation = await TryBeginCloudSafeShareMutationAsync(
                SelectedShare.Id);
            if (!cloudOperation.Acquired)
            {
                EditErrorMessage = Resources.Web_Error_ShareInUse;
                return false;
            }
            await using var cloudOperationLease = cloudOperation.Lease;
            var name = SelectedShare.Name;
            // Delete share-scoped lifecycle data before dropping the definition;
            // version cleanup also reclaims blobs that are no longer referenced.
            if (_versionService != null)
                await _versionService.DeleteShareAsync(SelectedShare.Id);
            await _aclService.DeleteShareMetadataAsync(SelectedShare.Id);
            await _shareRepo.DeleteAsync(SelectedShare.Id);
            _lockManager.RemoveLock(name);

            _logger.LogInformation("Share '{ShareName}' deleted", name);

            DeselectShare();
            await LoadAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting the share");
            EditErrorMessage = Resources.Web_Error_DeleteFailed;
            return false;
        }
    }

    public async Task DisconnectCurrentShareFromCloud()
    {
        if(SelectedShare is null)
            return;

        EditErrorMessage = null;
        if (!EnsureSelectedShareWritable())
            return;

        var cloudOperation = await TryBeginCloudSafeShareMutationAsync(
            SelectedShare.Id);
        if (!cloudOperation.Acquired)
        {
            EditErrorMessage = Resources.Web_Error_ShareInUse;
            return;
        }
        await using var cloudOperationLease = cloudOperation.Lease;

        SelectedShare.CloudConnection?.Dispose();
        SelectedShare.CloudSettings = new CloudSettings(new Dictionary<string, SyncedFolder>());
        SelectedShare.CloudConnection = null;
        await _shareRepo.UpdateAsync(SelectedShare);
    }

    private async Task<(bool Acquired, ICloudSyncOperationLease? Lease)>
        TryBeginCloudSafeShareMutationAsync(Guid shareId)
    {
        if (_cloudSyncOperations is null)
            return (true, null);

        var lease = await _cloudSyncOperations.TryBeginShareMutationAsync(
            shareId);
        return (lease is not null, lease);
    }
    
    // -- Access Management --

    public async Task LoadAccessAsync()
    {
        if (SelectedShare is null) return;

        AccessErrorMessage = null;

        try
        {
            ShowAccessPanel = true;
            AllUsers = (await _userRepo.GetAllAsync()).ToList();
            AllGroups = (await _groupRepo.GetAllAsync()).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading the access rights");
            AccessErrorMessage = Resources.Web_Error_LoadAccessRightsFailed;
        }
    }

    public string GetPrincipalDisplayName(Guid principalId)
    {
        var user = AllUsers.FirstOrDefault(u => u.Id == principalId);
        if (user is not null) return user.Name ?? user.Username;

        var group = AllGroups.FirstOrDefault(g => g.Id == principalId);
        if (group is not null) return group.Name;

        return principalId.ToString()[..8] + "…";
    }

    public string GetPrincipalType(Guid principalId)
    {
        if (AllUsers.Any(u => u.Id == principalId)) return "user";
        if (AllGroups.Any(g => g.Id == principalId)) return "group";
        return "unknown";
    }

    public sealed record StoragePoolItem(string Name, string Path);
}
