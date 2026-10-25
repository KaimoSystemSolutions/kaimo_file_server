using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Clouds;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Kaimo_File_Server.Web.Services;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Search;
using Kaimo_File_Server.Core.Language;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public partial class FileBrowserViewModel : IFileBrowserViewModel, IDisposable
{
    private readonly IFileServiceFactory _fileServiceFactory;
    private readonly IShareRepository _shareRepo;
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly IUserContextFactory _userContextFactory;
    private readonly IManagementAuthService _mgmtAuth;
    private readonly AuthenticationStateProvider _authState;
    private readonly ILogger<FileBrowserViewModel> _logger;
    private readonly IUserRepository _userRepo;
    private readonly FileDownloadTicketStore _downloadTickets;
    private readonly ZipDownloadTicketStore _zipTickets;
    private readonly DemoModeOptions _demo;
    private readonly ISyncDefinitionRepository _syncRepo;
    private readonly IShareLinkRepository _shareLinkRepo;
    private readonly ICloudSyncJobRunner? _syncJobs;

    private readonly ISearchService _searchService;

    // Not readonly: a derived view model (e.g. the public share browser) binds its own
    // file service to a fixed target instead of resolving it from a share name.
    protected IFileService? _fileService;

    // Exposed to derived view models so they can reuse the base file-operation methods.
    protected IUserContextFactory UserContextFactory => _userContextFactory;

    public FileBrowserViewModel(
        IFileServiceFactory fileServiceFactory,
        IShareRepository shareRepo,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IUserContextFactory userContextFactory,
        IManagementAuthService mgmtAuth,
        AuthenticationStateProvider authState,
        ILogger<FileBrowserViewModel> logger,
        ISearchService searchService,
        IUserRepository userRepo,
        FileDownloadTicketStore downloadTickets,
        ZipDownloadTicketStore zipTickets,
        DemoModeOptions demo,
        ISyncDefinitionRepository syncRepo,
        IShareLinkRepository shareLinkRepo,
        ICloudSyncJobRunner? syncJobs = null)
    {
        _fileServiceFactory = fileServiceFactory;
        _shareRepo = shareRepo;
        _dbFactory = dbFactory;
        _userContextFactory = userContextFactory;
        _mgmtAuth = mgmtAuth;
        _authState = authState;
        _logger = logger;
        _searchService = searchService;
        _userRepo = userRepo;
        _downloadTickets = downloadTickets;
        _zipTickets = zipTickets;
        _demo = demo;
        _syncRepo = syncRepo;
        _shareLinkRepo = shareLinkRepo;
        _syncJobs = syncJobs;
        if (_syncJobs is not null)
            _syncJobs.OnChanged += OnSyncJobsChanged;
    }

    // -- State --

    public event Action? OnStateChanged;

    public virtual BrowserCapabilities Capabilities
    {
        get
        {
            var caps = _demo.ReadOnly ? BrowserCapabilities.Local.AsReadOnly() : BrowserCapabilities.Local;
            // Cloud sync and the ACL editor are administrative features that do not belong in homes.
            return CurrentShare is { IsUserHomes: true } ? caps with { HasCloudSync = false, HasFileAcls = false } : caps;
        }
    }

    /// <summary>
    /// When false, share-link management is never offered regardless of the actor's
    /// permissions. Overridden by the anonymous public browser so a link never exposes a
    /// "share" action of its own. Default (true) keeps the normal per-share permission check.
    /// </summary>
    protected virtual bool AllowShareLinkManagement => true;

    /// <summary>
    /// The navigation root the browser is confined to (share-relative, no leading/trailing
    /// slash). "" = the whole share (normal behaviour). A derived browser can pin this to a
    /// sub-folder so navigation, the ".." action and the breadcrumb cannot climb above it.
    /// </summary>
    protected virtual string RootPath => "";

    /// <summary>
    /// The dedicated home browser may open the home-folder share; everywhere else it is treated
    /// as unknown unless the user holds <see cref="ManagementPermission.AccessHomeContents"/>.
    /// </summary>
    protected virtual bool IsHomeBrowser => false;

    public virtual BrowserShareInfo? CurrentBrowserShare => CurrentShare is null
        ? null
        : new BrowserShareInfo(CurrentShare.Id, CurrentShare.Name, BrowserShareKind.Local);

    public ShareDefinition? CurrentShare { get; private set; }

    public List<FileMetadata> Items { get; private set; } = [];
    public string CurrentPath { get; private set; } = "";
    public bool IsLoading { get; private set; }
    public string? ErrorMessage { get; private set; }

    /// <summary>
    /// Whether the current user may manage ACLs on the loaded share. This is a
    /// SCOPED decision (<see cref="ManagementPermission.ManageShareAcls"/> on this
    /// specific share) — not a coarse global-role check — so a department- or
    /// share-scoped manager only sees the ACL UI for shares in their scope.
    /// Drives the ACL panel, badges and permission actions in the view.
    /// </summary>
    public bool CanManageAcls { get; private set; }
    public bool CanManageSyncs { get; private set; }

    /// <summary>
    /// Scoped right to create/manage public share links on the loaded share
    /// (<see cref="ManagementPermission.ManageShareLinks"/>). Resolved per load, reset on
    /// every error/reset path — same pattern as <see cref="CanManageAcls"/>.
    /// </summary>
    public bool CanManageShareLinks { get; private set; }

    /// <summary>
    /// Scoped right to create/manage public upload links on the loaded share
    /// (<see cref="ManagementPermission.ManageUploadLinks"/>). Same lifecycle as
    /// <see cref="CanManageShareLinks"/>.
    /// </summary>
    public bool CanManageUploadLinks { get; private set; }

    // -- Computed --

    public IEnumerable<FileMetadata> Directories
        => Items.Where(f => f.IsDirectory).OrderBy(f => f.Name);

    public IEnumerable<FileMetadata> Files
        => Items.Where(f => !f.IsDirectory).OrderBy(f => f.Name);

    /// <summary>
    /// Maps a URL sub-path to a share-relative path. URL sub-paths are relative to the
    /// confined <see cref="RootPath"/>, so the real root folder is never part of the address.
    /// Identity when the browser is not confined.
    /// </summary>
    protected virtual string ResolveSubPath(string subPath)
        => RootPath.Length == 0 ? subPath
           : string.IsNullOrEmpty(subPath) ? RootPath
           : $"{RootPath}/{subPath}";

    /// <summary>
    /// Maps a share-relative path to the sub-path shown in the URL by stripping the confined
    /// root prefix. Identity when the browser is not confined.
    /// </summary>
    public virtual string RouteSubPathOf(string shareRelativePath)
    {
        if (RootPath.Length == 0) return shareRelativePath;
        if (string.Equals(shareRelativePath, RootPath, StringComparison.OrdinalIgnoreCase))
            return "";
        return shareRelativePath.StartsWith(RootPath + "/", StringComparison.OrdinalIgnoreCase)
            ? shareRelativePath[(RootPath.Length + 1)..]
            : shareRelativePath;
    }

    // True when the given share-relative path is the confined root or lives beneath it.
    private bool IsWithinRoot(string path)
        => RootPath.Length == 0
           || string.Equals(path, RootPath, StringComparison.OrdinalIgnoreCase)
           || path.StartsWith(RootPath + "/", StringComparison.OrdinalIgnoreCase);

    public bool HasParent
        => !string.IsNullOrEmpty(CurrentPath)
           && !string.Equals(CurrentPath, RootPath, StringComparison.OrdinalIgnoreCase);

    public string ParentPath
    {
        get
        {
            if (string.IsNullOrEmpty(CurrentPath)
                || string.Equals(CurrentPath, RootPath, StringComparison.OrdinalIgnoreCase))
                return RootPath;

            var lastSlash = CurrentPath.LastIndexOf('/');
            var parent = lastSlash <= 0 ? "" : CurrentPath[..lastSlash];
            // Never climb above the confined root.
            return IsWithinRoot(parent) ? parent : RootPath;
        }
    }

    public List<(string Name, string FullPath)> Breadcrumbs
    {
        get
        {
            if (string.IsNullOrEmpty(CurrentPath)) return [];

            // Only show the portion below the confined root; the share-root breadcrumb link
            // (rendered by the component) already stands in for the root folder itself.
            var relative = CurrentPath;
            if (RootPath.Length > 0)
            {
                if (string.Equals(CurrentPath, RootPath, StringComparison.OrdinalIgnoreCase))
                    return [];
                if (CurrentPath.StartsWith(RootPath + "/", StringComparison.OrdinalIgnoreCase))
                    relative = CurrentPath[(RootPath.Length + 1)..];
            }

            var parts = relative.Split('/');
            var result = new List<(string, string)>();
            for (int i = 0; i < parts.Length; i++)
            {
                // FullPath stays absolute share-relative so navigation still targets the
                // real path (the confined-root prefix is preserved).
                var below = string.Join('/', parts[..(i + 1)]);
                var full = RootPath.Length > 0 ? $"{RootPath}/{below}" : below;
                result.Add((parts[i], full));
            }

            return result;
        }
    }

    // -- Commands --

    public async Task LoadShareAsync(string shareName, string subPath = "")
    {
        TrackUndoLocation(shareName, subPath);
        try
        {
            IsLoading = true;
            ErrorMessage = null;
            CanManageAcls = false;
            CanManageSyncs = false;
            CanManageShareLinks = false;
            CanManageUploadLinks = false;
            CanManageUploadLinks = false;
            Interlocked.Increment(ref _syncLoadGeneration);
            _shareSyncs = [];
            _activeSyncPaths = [];
            _sharedRoots = [];
            _fileService = null;

            // Clear the previously loaded location up front so the breadcrumb does
            // not keep showing the last opened share/path while the new target loads.
            CurrentShare = null;
            CurrentPath = "";

            // Resolved into a local first: the permission check awaits, and an overlapping load
            // resets CurrentShare meanwhile.
            var share = await _shareRepo.GetByNameAsync(shareName);
            if (share is { IsUserHomes: true } && !IsHomeBrowser && !await CanBrowseAllHomesAsync())
                share = null;
            CurrentShare = share;
            if (CurrentShare is null)
            {
                ErrorMessage = Resources.Web_Error_ShareNotFound;
                Items = [];
                return;
            }

            // A disabled share is unavailable to everyone, including share managers.
            // Enforce this before creating a file service so a direct /files/{share}
            // URL cannot be used to reach its contents.
            if (!CurrentShare.IsEnabled)
            {
                ErrorMessage = Resources.Web_Error_ShareDisabled;
                Items = [];
                return;
            }

            _fileService = _fileServiceFactory.CreateForShare(CurrentShare);

            var cleanSub = (subPath ?? "").Trim('/');

            var sharePath = CurrentShare.Path.Trim('/');
            if (cleanSub.Equals(sharePath, StringComparison.OrdinalIgnoreCase))
                cleanSub = "";
            else if (cleanSub.StartsWith(sharePath + "/", StringComparison.OrdinalIgnoreCase))
                cleanSub = cleanSub[(sharePath.Length + 1)..];

            CurrentPath = ResolveSubPath(cleanSub);

            if (!ShareRelativePath.IsValid(CurrentPath))
            {
                ErrorMessage = Resources.Web_Error_InvalidPath;
                Items = [];
                return;
            }

            // Confine navigation to the browser's root (default "" = whole share). A path that
            // would escape the shared folder is clamped back to the root, never followed.
            if (!IsWithinRoot(CurrentPath))
                CurrentPath = RootPath;

            var userContext = await GetCurrentUserContextAsync();
            if (userContext is null)
            {
                ErrorMessage = Resources.Web_Error_NotAuthenticated;
                Items = [];
                return;
            }

            // Scoped ACL-management right for THIS share, resolved once per load and
            // reused by the view. Same authority source the ACL editor enforces on write.
            // Home-folder ACLs are owned by the home-folder service (the ACL editor refuses them too).
            CanManageAcls = !CurrentShare.IsUserHomes && await _mgmtAuth.CanManageShareAsync(
                userContext, CurrentShare.Id, ManagementPermission.ManageShareAcls);

            CanManageShareLinks = AllowShareLinkManagement
                && await _mgmtAuth.CanManageShareAsync(
                    userContext, CurrentShare.Id, ManagementPermission.ManageShareLinks);
            CanManageUploadLinks = AllowShareLinkManagement
                && await _mgmtAuth.CanManageShareAsync(
                    userContext, CurrentShare.Id, ManagementPermission.ManageUploadLinks);

            // Which items in this share carry a public link, so the browser can mark them.
            // Skipped for the public browser (AllowShareLinkManagement == false).
            _sharedRoots = AllowShareLinkManagement
                ? (await _shareLinkRepo.ListForSharesAsync(new[] { CurrentShare.Id }))
                    .Where(l => l.IsEnabled)
                    .GroupBy(l => (Root: ShareRelativePath.Normalize(l.RootRelativePath), l.Kind))
                    .Select(g => (Root: g.Key.Root, LinkId: g.First().Id, g.Key.Kind))
                    .ToList()
                : [];

            // Sync state is internal configuration: a browser without cloud-sync support (the
            // anonymous public-link browser) never loads it, so no sync marker can be rendered.
            if (Capabilities.HasCloudSync)
            {
                CanManageSyncs = await _mgmtAuth.HasAnyPermissionAsync(userContext, ManagementPermission.SyncAdmin);
                await LoadShareSyncsAsync(CurrentShare.Id);
            }

            _logger.LogDebug("Loading path: '{CurrentPath}' (share={ShareName}, user={User})",
                CurrentPath, shareName, userContext.User.Username);

            Items = await _fileService.ListAsync(CurrentPath, userContext);
            await LoadHomeOwnerNamesAsync();

            _logger.LogDebug("Found {Total} items ({Dirs} dirs, {Files} files)",
                Items.Count, Directories.Count(), Files.Count());

            _ = LoadDirectorySizesInBackgroundAsync();
        }
        catch (DirectoryNotFoundException)
        {
            ErrorMessage = Resources.Web_Error_FileOrFolderNotFound;
            CanManageAcls = false;
            CanManageSyncs = false;
            CanManageShareLinks = false;
            CanManageUploadLinks = false;
            Items = [];
        }
        catch (UnauthorizedAccessException)
        {
            ErrorMessage = Resources.Web_Error_AccessDenied;
            CanManageAcls = false;
            CanManageSyncs = false;
            CanManageShareLinks = false;
            CanManageUploadLinks = false;
            Items = [];
        }
        catch (Exception ex)
        {
            ErrorMessage = Resources.Web_Error_LoadFilesFailed;
            CanManageAcls = false;
            CanManageSyncs = false;
            CanManageShareLinks = false;
            CanManageUploadLinks = false;
            _logger.LogError(ex, "Error loading share {ShareName} path {SubPath}", shareName, subPath);
            Items = [];
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Refreshes only the already-open directory. Unlike <see cref="LoadShareAsync"/>,
    /// this keeps the current file service and cached share/management context, avoiding
    /// redundant database and authorization work after a local file operation.
    /// </summary>
    public async Task RefreshCurrentDirectoryAsync()
    {
        if (_fileService is null || CurrentShare is null)
            return;

        var userContext = await GetCurrentUserContextAsync();
        if (userContext is null)
        {
            ErrorMessage = Resources.Web_Error_NotAuthenticated;
            return;
        }

        try
        {
            ErrorMessage = null;
            Items = await _fileService.ListAsync(CurrentPath, userContext);
            _ = LoadDirectorySizesInBackgroundAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = Resources.Web_Error_LoadFilesFailed;
            _logger.LogError(
                ex,
                "Error refreshing share {ShareName} path {SubPath}",
                CurrentShare.Name,
                CurrentPath);
        }
    }

    /// <summary>
    /// Resolves the identity every file operation runs under. The default binds to the
    /// signed-in circuit user; the anonymous public browser overrides this to run under the
    /// link creator's fixed identity.
    /// </summary>
    protected virtual async Task<UserContext?> GetCurrentUserContextAsync()
    {
        var state = await _authState.GetAuthenticationStateAsync();
        var username = state.User.Identity?.Name;
        if (string.IsNullOrEmpty(username)) return null;
        return await _userContextFactory.CreateByUsernameAsync(username);
    }

    /// <summary>
    /// Resolves the active file service and authenticated user for operations that
    /// require a loaded share and a signed-in user. Throws when either precondition is
    /// not met, so callers can rely on non-null results.
    /// </summary>
    private async Task<(IFileService Service, UserContext User)> RequireServiceAndUserAsync()
    {
        if (_fileService is null)
            throw new InvalidOperationException("No share is currently loaded.");

        var userContext = await GetCurrentUserContextAsync()
            ?? throw new InvalidOperationException("No authenticated user context is available.");

        return (_fileService, userContext);
    }

    /// <summary>
    /// Looks up the persisted metadata record (stable <see cref="FileMetadata.Id"/> and
    /// <see cref="FileMetadata.OwnerId"/>) for a browsed item.
    ///
    /// Filesystem listings are the source of truth for browsing and carry no database
    /// identity — their <c>Id</c> and <c>OwnerId</c> are always <see cref="Guid.Empty"/>.
    /// A backing row is only created lazily (e.g. the first time an ACL is assigned via
    /// <c>IFileMetadataRepository.GetOrCreateAsync</c>). Returns <c>null</c> when no row
    /// exists yet, so callers can avoid presenting a meaningless all-zero identifier.
    /// </summary>
    public async Task<(Guid Id, Guid OwnerId)?> GetPersistedMetadataAsync(FileMetadata item)
    {
        if (CurrentShare is null) return null;

        var normalized = Kaimo_File_Server.Core.Helpers.ShareRelativePath.Normalize(ToShareRelative(item.Path));

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var row = await db.FileMetadata
                .Where(m => m.ShareId == CurrentShare.Id && m.Path == normalized)
                .Select(m => new { m.Id, m.OwnerId })
                .FirstOrDefaultAsync();

            return row is null ? null : (row.Id, row.OwnerId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load persisted metadata for {Path}", item.Path);
            return null;
        }
    }

    /// <summary>
    /// Resolves the display name of an item's owner for the properties dialog.
    /// Falls back to a short id fragment when the owning user cannot be found
    /// (e.g. a deleted account) and returns null for the empty/unset owner.
    /// </summary>
    public async Task<string?> GetOwnerDisplayNameAsync(Guid ownerId)
    {
        if (ownerId == Guid.Empty) return null;

        try
        {
            var user = await _userRepo.GetByIdAsync(ownerId);
            if (user is not null)
                return string.IsNullOrWhiteSpace(user.Name) ? user.Username : user.Name;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve owner {OwnerId}", ownerId);
        }

        return ownerId.ToString()[..8] + "…";
    }

    public void Dispose()
    {
        if (_syncJobs is not null)
            _syncJobs.OnChanged -= OnSyncJobsChanged;

        List<string> staged;
        lock (_uploadLock)
        {
            staged = _pendingConflicts.Select(c => c.StagedTempPath).ToList();
            _pendingConflicts.Clear();
        }
        foreach (var path in staged) TryDeleteStaged(path);
    }
}
