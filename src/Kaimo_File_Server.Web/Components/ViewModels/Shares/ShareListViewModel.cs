using Kaimo_File_Server.Infrastructure.Services;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Storage;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Search;
using Kaimo_File_Server.Web.DynamicHelpers;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
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
    private readonly IConfigRepository? _config;
    private readonly IDepartmentRepository? _departmentRepo;
    private readonly SharePoolMoveService _poolMove;

    // Admin-assigned pool labels (normalized path → friendly name), refreshed in
    // LoadAsync. Empty until loaded, which falls back to path-derived names.
    private IReadOnlyDictionary<string, string> _customPoolNames =
        new Dictionary<string, string>();

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
        ICloudSyncOperationCoordinator? cloudSyncOperations = null,
        IConfigRepository? config = null,
        IDepartmentRepository? departmentRepo = null,
        SharePoolMoveService? poolMove = null)
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
            .Select(path => new StoragePoolItem(StoragePoolNaming.DerivedName(path), path))
            .ToList();
        NewSharePoolPath = storagePools.FirstOrDefault() ?? string.Empty;
        _versionService = versionService;
        _searchService = searchService;
        _cloudSyncOperations = cloudSyncOperations;
        _config = config;
        _departmentRepo = departmentRepo;
        _poolMove = poolMove ?? new SharePoolMoveService(
            shareRepo, lockManager, NullLogger<SharePoolMoveService>.Instance,
            searchService, cloudSyncOperations);
    }

    /// <summary>
    /// Reloads the admin-assigned pool labels and rebuilds <see cref="StoragePools"/>
    /// so the create/move dropdowns and share cards show the friendly names.
    /// </summary>
    private async Task RefreshPoolNamesAsync()
    {
        if (_config is null) return;

        _customPoolNames = await _config.GetAsync(
            StoragePoolNaming.ConfigKey, new Dictionary<string, string>());
        StoragePools = _storagePools
            .Select(path => new StoragePoolItem(
                StoragePoolNaming.Resolve(_customPoolNames, path), path))
            .ToList();
    }

    // -- State --

    public List<ShareDefinition> Shares { get; private set; } = [];
    public bool IsLoading { get; private set; }
    public string? ErrorMessage { get; private set; }

    // -- Edit --

    public ShareDefinition? SelectedShare { get; private set; }
    public string EditShareName { get; set; } = "";
    public string EditSharePoolPath { get; set; } = "";
    public string? EditErrorMessage { get; private set; }
    public string? EditSuccessMessage { get; private set; }
    public bool ShowDeleteConfirm { get; set; }
    public bool IsRenaming { get; private set; }
    public bool IsMovingPool { get; private set; }

    // -- Department --

    // All departments (global first) for the settings picker; empty when no
    // department repository is wired (keeps the section hidden).
    public List<Department> Departments { get; private set; } = [];
    // DepartmentId → department, for the list-view chip lookup.
    private IReadOnlyDictionary<Guid, Department> _departmentsById =
        new Dictionary<Guid, Department>();
    // The department currently chosen in the picker for the selected share.
    public Guid EditShareDepartmentId { get; set; }
    public bool ShowDepartmentConfirm { get; set; }

    public IReadOnlyList<StoragePoolItem> StoragePools { get; private set; }

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

            await RefreshPoolNamesAsync();
            if (_config is not null)
                GlobalRecycleMaxSizeBytes = await _config.GetAsync(RecycleBinJanitor.GlobalMaxSizeConfigKey, 0L);

            if (_departmentRepo is not null)
            {
                Departments = (await _departmentRepo.GetAllAsync())
                    .OrderByGlobalFirst().ToList();
                _departmentsById = Departments.ToDictionary(d => d.Id);
            }

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
                // The home-folder share is system-managed (settings + /files/users) and is
                // never renamed, moved, disabled or re-permissioned from share management.
                if (share.IsUserHomes)
                    continue;

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

        if (!SambaName.IsValidShareName(name) || IsReservedSystemShareName(name))
        {
            error = string.Format(Resources.Web_Validation_ReservedName, name);
            return false;
        }

        return true;
    }

    // "users" is the home-folder share and "user" the web route of a user's own home.
    private static bool IsReservedSystemShareName(string name)
        => name.Equals(HomeDirectoryService.ShareName, StringComparison.OrdinalIgnoreCase)
           || name.Equals("user", StringComparison.OrdinalIgnoreCase);

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
    /// The department a share belongs to, with its palette colors for the list-view
    /// chip. Mirrors <c>ShareBrowserViewModel.GetDepartment</c> so the management list
    /// and the file browser share overview render the same chip. Returns <c>null</c>
    /// when the department is unknown, so the caller shows a plain placeholder.
    /// </summary>
    public DepartmentDisplay? GetDepartmentDisplay(ShareDefinition share)
    {
        if (!_departmentsById.TryGetValue(share.DepartmentId, out var department))
            return null;

        var (color, soft) = DepartmentPalette.For(department.Id, department.Color);
        return new DepartmentDisplay(
            string.IsNullOrWhiteSpace(department.Name) ? "—" : department.Name, color, soft);
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
        EditShareDepartmentId = share.DepartmentId;
        EditErrorMessage = null;
        EditSuccessMessage = null;
        ShowDeleteConfirm = false;
        ShowDepartmentConfirm = false;
        ShowAccessPanel = false;
        IsCreating = false;
    }

    public void DeselectShare()
    {
        SelectedShare = null;
        EditShareName = "";
        EditSharePoolPath = "";
        EditShareDepartmentId = Guid.Empty;
        EditErrorMessage = null;
        EditSuccessMessage = null;
        ShowDeleteConfirm = false;
        ShowDepartmentConfirm = false;
        ShowAccessPanel = false;
    }

    public sealed record StoragePoolItem(string Name, string Path);
}
