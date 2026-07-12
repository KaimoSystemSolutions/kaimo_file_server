using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Storage;
using Microsoft.AspNetCore.Components.Authorization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Kaimo_File_Server.Core.Language;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public partial class ShareListViewModel
{
    private readonly IShareRepository _shareRepo;
    private readonly IUserRepository _userRepo;
    private readonly IGroupRepository _groupRepo;
    private readonly IAclRepository _aclRepo;
    private readonly IFileMetadataRepository _metaRepo;
    private readonly IAclService _aclService;
    private readonly IManagementAuthService _mgmtAuth;
    private readonly IUserContextFactory _userContextFactory;
    private readonly IStorageEngine _storage;
    private readonly ShareLockManager _lockManager;
    private readonly AuthenticationStateProvider _authState;
    private readonly ILogger<ShareListViewModel> _logger;
    private readonly string _storagePath;

    public ShareListViewModel(
        IShareRepository shareRepo,
        IUserRepository userRepo,
        IGroupRepository groupRepo,
        IAclRepository aclRepo,
        IFileMetadataRepository metaRepo,
        IAclService aclService,
        IManagementAuthService mgmtAuth,
        IUserContextFactory userContextFactory,
        IStorageEngine storage,
        ShareLockManager lockManager,
        AuthenticationStateProvider authState,
        ILogger<ShareListViewModel> logger,
        string storagePath)
    {
        _shareRepo = shareRepo;
        _userRepo = userRepo;
        _groupRepo = groupRepo;
        _aclRepo = aclRepo;
        _metaRepo = metaRepo;
        _aclService = aclService;
        _mgmtAuth = mgmtAuth;
        _userContextFactory = userContextFactory;
        _storage = storage;
        _lockManager = lockManager;
        _authState = authState;
        _logger = logger;
        _storagePath = storagePath.TrimEnd('/');
    }

    // -- State --

    public List<ShareDefinition> Shares { get; private set; } = [];
    public bool IsLoading { get; private set; }
    public string? ErrorMessage { get; private set; }

    // -- Create --

    public bool IsCreating { get; set; }
    public string NewShareName { get; set; } = "";
    public string? CreateErrorMessage { get; private set; }

    // -- Edit --

    public ShareDefinition? SelectedShare { get; private set; }
    public string EditShareName { get; set; } = "";
    public string? EditErrorMessage { get; private set; }
    public string? EditSuccessMessage { get; private set; }
    public bool ShowDeleteConfirm { get; set; }
    public bool IsRenaming { get; private set; }

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

        if (name.Length > 64)
        { error = Resources.Web_ShareName_MaxLength; return false; }

        if (!SafeShareNameRegex().IsMatch(name))
        { error = Resources.Web_ShareName_AllowedChars; return false; }

        if (name.StartsWith('.') || name.EndsWith('.'))
        { error = Resources.Web_ShareName_NoLeadingTrailingDot; return false; }

        return true;
    }

    private string BuildSharePath(string name) => $"{_storagePath}/{name}";

    // -- Create --

    public async Task<bool> CreateShareAsync()
    {
        CreateErrorMessage = null;
        var name = NewShareName.Trim();

        if (!ValidateShareName(name, out var error))
        { CreateErrorMessage = error; return false; }

        try
        {
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

            var share = new ShareDefinition(name, BuildSharePath(name));
            await _shareRepo.CreateAsync(share);
            await _storage.CreateDirectoryAsync(name);

            // Root-FileMetadata persistieren (Path == "", Konvention aus
            // ShareRelativePath) und die Owner-FullControl-ACL daran hängen.
            // GetOrCreateAsync schreibt die Metadata-Zeile tatsächlich in die DB —
            // vorher wurde nur die ACL angelegt, deren FileMetadata nie existierte.
            var rootMeta = await _metaRepo.GetOrCreateAsync(
                "", isDirectory: true, userId: user.Id, shareId: share.Id);

            var newAcl = new AccessEntry(
                user.Id,
                AclEntryType.Allow,
                FilePermission.FullControl,
                AclInheritance.AllDescendants)
            {
                FileMetadataId = rootMeta.Id
            };

            await _aclRepo.AddAsync(newAcl);

            _logger.LogInformation("Share '{ShareName}' created", name);

            NewShareName = "";
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
                // 1. Physischen Ordner umbenennen
                var oldFullPath = Path.Combine(_storagePath, oldName);
                var newFullPath = Path.Combine(_storagePath, newName);

                if (Directory.Exists(oldFullPath))
                    Directory.Move(oldFullPath, newFullPath);

                // 2. ShareDefinition in DB updaten
                SelectedShare.Name = newName;
                SelectedShare.Path = BuildSharePath(newName);
                await _shareRepo.UpdateAsync(SelectedShare);

                // 4. Lock-Key umbenennen
                _lockManager.RenameLock(oldName, newName);

                _logger.LogInformation("Share '{OldName}' renamed to '{NewName}'", oldName, newName);

                EditSuccessMessage = string.Format(Resources.Web_Share_RenamedTo, newName);
                EditShareName = newName;
                await LoadAsync();

                // Re-select mit neuen Daten
                var updated = Shares.FirstOrDefault(s => s.Id == SelectedShare.Id);
                if (updated is not null)
                    SelectedShare = updated;

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

    // -- Toggle Share Enabled and Recycle Enabled and Share Hidden --

    public async Task<bool> ToggleShareEnabledAsync()
    {
        if (SelectedShare is null) return false;

        EditErrorMessage = null;
        EditSuccessMessage = null;

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

        // Server-side authorization: the razor hides the button when the actor
        // cannot manage this share, but the view-model method must guard too —
        // it enforces the specific DeleteShares right AND the department scope.
        if (!await CanManageSelectedShareAsync(ManagementPermission.DeleteShares))
        { EditErrorMessage = Resources.Web_Error_NoPermission; return false; }

        try
        {
            var name = SelectedShare.Name;
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
}