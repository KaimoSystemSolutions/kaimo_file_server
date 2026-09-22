using System.Collections.Concurrent;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Security.Claims;
using Kaimo_File_Server.Web.Helpers;
using Kaimo_File_Server.Web.Services;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Search;
using Kaimo_File_Server.Core.Language;
using System.Diagnostics;
using System.Security.Cryptography;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>Simple result wrapper for UI operations.</summary>
public record OperationResult(bool Success, string? Error = null)
{
    public static OperationResult Ok() => new(true);
    public static OperationResult Fail(string error) => new(false, error);
}

public class FileBrowserViewModel : IFileBrowserViewModel, IDisposable
{
    private long MaxUploadSizeBytes => 1100L * 1024 * 1024; // 1.1 GB

    // Inline preview buffers the whole file into a server-side byte[]; cap it so a
    // huge file can never blow up server memory. Larger files fall back to download.
    private long MaxPreviewSizeBytes => 25L * 1024 * 1024; // 25 MB

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

    // Normalized root paths of the loaded share's public links (with the owning link's id, so
    // an emblem can jump to it), so the browser can mark a shared folder (and everything beneath
    // it). Loaded per share; empty for the public browser.
    private List<(string Root, Guid LinkId)> _sharedRoots = [];

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
        IShareLinkRepository shareLinkRepo)
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
    }

    // -- State --

    public event Action? OnStateChanged;

    public virtual BrowserCapabilities Capabilities =>
        _demo.ReadOnly ? BrowserCapabilities.Local.AsReadOnly() : BrowserCapabilities.Local;

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

    public BrowserShareInfo? CurrentBrowserShare => CurrentShare is null
        ? null
        : new BrowserShareInfo(CurrentShare.Id, CurrentShare.Name, BrowserShareKind.Local);

    // Written from a background task (bounded parallelism) while the render thread
    // reads it — hence concurrent, not a plain Dictionary, to avoid torn reads.
    public ConcurrentDictionary<string, long> DirectorySizes { get; private set; } = new();
    private CancellationTokenSource? _sizeCts;

    // Directory-size calculation walks the disk per folder; cap the fan-out and
    // coalesce UI refreshes so a folder with many sub-directories doesn't trigger
    // one full re-render per sub-directory.
    private const int MaxConcurrentSizeCalculations = 4;
    private static readonly TimeSpan SizeUpdateThrottle = TimeSpan.FromMilliseconds(150);
    public Dictionary<string, int> AclCounts { get; private set; } = new();
    public ShareDefinition? CurrentShare { get; private set; }

    // Sync local destinations for the loaded share, ordered most-specific (longest
    // LocalPath) first so a nested sync wins over an ancestor one. Loaded per share,
    // not per directory, and reused across in-place directory refreshes.
    private List<(string LocalPath, SyncMode Mode, string Name, DateTime? LastSuccessfulRunAtUtc, HashSet<string>? RemotePaths, Guid Id)> _shareSyncs = [];

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

    // -- Computed --

    public IEnumerable<FileMetadata> Directories
        => Items.Where(f => f.IsDirectory).OrderBy(f => f.Name);

    public IEnumerable<FileMetadata> Files
        => Items.Where(f => !f.IsDirectory).OrderBy(f => f.Name);

    /// <summary>
    /// Maps a URL sub-path to a share-relative path. Identity for the default browser; the
    /// public share VM overrides it to treat URL sub-paths as relative to the confined root.
    /// </summary>
    protected virtual string ResolveSubPath(string subPath) => subPath;

    /// <summary>
    /// Maps a share-relative path to the sub-path shown in the URL. Identity for the default
    /// browser; the public share VM strips the confined-root prefix so it never leaks.
    /// </summary>
    public virtual string RouteSubPathOf(string shareRelativePath) => shareRelativePath;

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
        try
        {
            IsLoading = true;
            ErrorMessage = null;
            CanManageAcls = false;
            CanManageSyncs = false;
            CanManageShareLinks = false;
            _shareSyncs = [];
            _sharedRoots = [];
            _fileService = null;

            // Clear the previously loaded location up front so the breadcrumb does
            // not keep showing the last opened share/path while the new target loads.
            CurrentShare = null;
            CurrentPath = "";

            CurrentShare = await _shareRepo.GetByNameAsync(shareName);
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

            _fileService = _fileServiceFactory.CreateForShare(CurrentShare.Id, CurrentShare.Path);

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
            CanManageAcls = await _mgmtAuth.CanManageShareAsync(
                userContext, CurrentShare.Id, ManagementPermission.ManageShareAcls);

            CanManageShareLinks = AllowShareLinkManagement
                && await _mgmtAuth.CanManageShareAsync(
                    userContext, CurrentShare.Id, ManagementPermission.ManageShareLinks);

            // Which items in this share carry a public link, so the browser can mark them.
            // Skipped for the public browser (AllowShareLinkManagement == false).
            _sharedRoots = AllowShareLinkManagement
                ? (await _shareLinkRepo.ListForSharesAsync(new[] { CurrentShare.Id }))
                    .Where(l => l.IsEnabled)
                    .GroupBy(l => ShareRelativePath.Normalize(l.RootRelativePath))
                    .Select(g => (Root: g.Key, LinkId: g.First().Id))
                    .ToList()
                : [];

            CanManageSyncs = await _mgmtAuth.HasAnyPermissionAsync(userContext, ManagementPermission.SyncAdmin);

            await LoadShareSyncsAsync(CurrentShare.Id);

            _logger.LogDebug("Loading path: '{CurrentPath}' (share={ShareName}, user={User})",
                CurrentPath, shareName, userContext.User.Username);

            Items = await _fileService.ListAsync(CurrentPath, userContext);
            
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
            Items = [];
        }
        catch (UnauthorizedAccessException)
        {
            ErrorMessage = Resources.Web_Error_AccessDenied;
            CanManageAcls = false;
            CanManageSyncs = false;
            CanManageShareLinks = false;
            Items = [];
        }
        catch (Exception ex)
        {
            ErrorMessage = Resources.Web_Error_LoadFilesFailed;
            CanManageAcls = false;
            CanManageSyncs = false;
            CanManageShareLinks = false;
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


    public async Task CreateFolderAtAsync(string path)
    {
        var (service, user) = await RequireServiceAndUserAsync();
        await service.CreateDirectoryAsync(path, user);
    }

    /// <summary>Create a new sub-folder inside the current directory.</summary>
    public async Task<OperationResult> CreateFolderAsync(string folderName)
    {
        if (_fileService is null || CurrentShare is null)
            return OperationResult.Fail(Resources.Web_Error_NoShareLoaded);

        if (string.IsNullOrWhiteSpace(folderName))
            return OperationResult.Fail(Resources.Web_Folder_NameRequired);

        // File- / Directoryname Validation
        if (!WindowsFileNameHelper.IsValid(folderName))
        {
            var errors = WindowsFileNameHelper.GetValidationErrors(folderName);
            return OperationResult.Fail(Resources.Web_Name_InvalidChars + string.Join(", ", errors));
        }

        try
        {
            var userContext = await GetCurrentUserContextAsync();
            if (userContext is null)
                return OperationResult.Fail(Resources.Web_Error_NotAuthenticated);

            var targetPath = GetCurrentPath(folderName);

            await _fileService.CreateDirectoryAsync(targetPath, userContext);

            _logger.LogInformation("Folder created: '{Path}' by {User}",
                targetPath, userContext.User.Username);

            return OperationResult.Ok();
        }
        catch (UnauthorizedAccessException)
        {
            return OperationResult.Fail(Resources.Web_Error_AccessDenied);
        }
        catch (CloudSyncOperationConflictException)
        {
            return OperationResult.Fail(Resources.Web_Error_ShareInUse);
        }
        catch (IOException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult.Fail(Resources.Web_Error_ItemExists);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating folder '{FolderName}'", folderName);
            return OperationResult.Fail(Resources.Web_Error_CreateFolderFailed);
        }
    }

    /// <summary>Delete a file or directory (recursively).</summary>
    public async Task<OperationResult> DeleteAsync(FileMetadata item)
    {
        if (_fileService is null || CurrentShare is null)
            return OperationResult.Fail(Resources.Web_Error_NoShareLoaded);

        try
        {
            var userContext = await GetCurrentUserContextAsync();
            if (userContext is null)
                return OperationResult.Fail(Resources.Web_Error_NotAuthenticated);

            // Build relative path within the share
            var relativePath = item.Path;
            if (relativePath.StartsWith(CurrentShare.Path))
                relativePath = relativePath[CurrentShare.Path.Length..].TrimStart('/');

            await _fileService.DeleteFileAsync(relativePath, userContext, CurrentShare.IsRecycleEnabled);

            _logger.LogInformation("{Type} deleted: '{Path}' by {User}",
                item.IsDirectory ? "Directory" : "File",
                relativePath, userContext.User.Username);

            return OperationResult.Ok();
        }
        catch (UnauthorizedAccessException)
        {
            return OperationResult.Fail(Resources.Web_Error_AccessDenied);
        }
        catch (FileNotFoundException)
        {
            return OperationResult.Fail(Resources.Web_Error_FileOrFolderNotFound);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting '{Path}'", item.Path);
            return OperationResult.Fail(Resources.Web_Error_DeleteFailed);
        }
    }

    public async Task<OperationResult> RenameAsync(FileMetadata item, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            return OperationResult.Fail(Resources.Web_Rename_NameRequired);

        if (!WindowsFileNameHelper.IsValid(newName))
        {
            var errors = WindowsFileNameHelper.GetValidationErrors(newName);
            return OperationResult.Fail(
                Resources.Web_Name_InvalidChars + string.Join(", ", errors));
        }

        if (CurrentShare is null)
            return OperationResult.Fail(Resources.Web_Error_NoShareLoaded);

        var relativePath = item.Path;
        if (relativePath.StartsWith(CurrentShare.Path))
            relativePath = relativePath[CurrentShare.Path.Length..].TrimStart('/');

        var parentDir = Kaimo_File_Server.Core.Helpers.ShareRelativePath.GetParent(relativePath);
        var newRelativePath = Kaimo_File_Server.Core.Helpers.ShareRelativePath.Combine(parentDir, newName);

        return await MoveInternalAsync(item, relativePath, newRelativePath, "renamed");
    }

    /// <summary>
    /// Moves an item into a different directory within the same share, keeping its name.
    /// </summary>
    /// <param name="item">The item to move.</param>
    /// <param name="destRelativePath">Share-relative path of the destination directory ("" = share root).</param>
    public async Task<OperationResult> MoveAsync(FileMetadata item, string destRelativePath)
    {
        if (CurrentShare is null)
            return OperationResult.Fail(Resources.Web_Error_NoShareLoaded);

        var relativePath = item.Path;
        if (relativePath.StartsWith(CurrentShare.Path))
            relativePath = relativePath[CurrentShare.Path.Length..].TrimStart('/');

        var newRelativePath = Kaimo_File_Server.Core.Helpers.ShareRelativePath.Combine(destRelativePath, item.Name);

        // No-op: dropped onto the folder it's already in
        var currentParent = Kaimo_File_Server.Core.Helpers.ShareRelativePath.GetParent(relativePath);
        if (string.Equals(currentParent, destRelativePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            return OperationResult.Ok();

        return await MoveInternalAsync(item, relativePath, newRelativePath, "moved");
    }

    public async Task<List<FileMetadata>> ListDirectoryAsync(string dirPath)
    {
        var (service, user) = await RequireServiceAndUserAsync();
        return await service.ListAsync(dirPath, user);
    }

    public async Task CopyAsync(FileMetadata item, string targetPath, CancellationToken cancellationToken)
    {
        var (service, user) = await RequireServiceAndUserAsync();
        Stream fileToCopy = await service.ReadFileAsync(item.Path, user);
        await service.WriteFileAsync(targetPath, fileToCopy, user, cancellationToken);
    }

    private string GetCurrentPath(string fileName)
    {
        return string.IsNullOrEmpty(CurrentPath) 
            ? fileName
            : $"{CurrentPath}/{fileName}";
    }

    /// <summary>
    /// Shared implementation for Rename and Move: both are just "relocate item from
    /// one share-relative path to another" as far as the file service is concerned.
    /// </summary>
    private async Task<OperationResult> MoveInternalAsync(
        FileMetadata item, string relativePath, string newRelativePath, string logVerb)
    {
        if (_fileService is null)
            return OperationResult.Fail(Resources.Web_Error_NoShareLoaded);

        try
        {
            var userContext = await GetCurrentUserContextAsync();
            if (userContext is null)
                return OperationResult.Fail(Resources.Web_Error_NotAuthenticated);

            await _fileService.RenameAsync(relativePath, newRelativePath, userContext);

            _logger.LogInformation("{Type} {Verb}: '{OldPath}' -> '{NewPath}' by {User}",
                item.IsDirectory ? "Directory" : "File",
                logVerb, relativePath, newRelativePath, userContext.User.Username);

            return OperationResult.Ok();
        }
        catch (UnauthorizedAccessException)
        {
            return OperationResult.Fail(Resources.Web_Error_AccessDenied);
        }
        catch (IOException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult.Fail(Resources.Web_Error_ItemExists);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error relocating '{Path}' to '{NewPath}'", relativePath, newRelativePath);
            return OperationResult.Fail(logVerb == "moved"
                ? Resources.Web_Error_MoveFailed
                : Resources.Web_Error_RenameFailed);
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

    /// <summary>Load ACL counts for current path + all visible items in one DB call.</summary>
    public async Task LoadAclCountsAsync()
    {
        AclCounts.Clear();

        // Counts are only ever rendered for ACL managers; skip the DB query entirely
        // for everyone else instead of computing data the view will not show.
        if (CurrentShare is null || !CanManageAcls) return;

        var paths = new List<string> { CurrentPath ?? "" };

        foreach (var item in Items)
        {
            var relativePath = item.Path;
            if (relativePath.StartsWith(CurrentShare.Path))
                relativePath = relativePath[CurrentShare.Path.Length..].TrimStart('/');
            paths.Add(relativePath);
        }

        var distinctPaths = paths.Distinct().ToList();

        await using var db = await _dbFactory.CreateDbContextAsync();

        try
        {
            AclCounts = await db.FileMetadata
                .Where(fm => fm.ShareId == CurrentShare.Id && distinctPaths.Contains(fm.Path))
                .Select(fm => new { fm.Path, Count = fm.Acl.Count })
                .ToDictionaryAsync(x => x.Path, x => x.Count);
        }
        catch (Exception ex)
        {
            // FIX: was silently swallowed — now logged so failures are visible
            _logger.LogWarning(ex, "Failed to load ACL counts for share '{ShareName}'",
                CurrentShare.Name);
        }
    }

    public int GetAclCount(string path)
    {
        return AclCounts.TryGetValue(path, out var count) ? count : 0;
    }

    /// <summary>
    /// Computes directory sizes off the render path. Sizes are calculated with bounded
    /// parallelism and pushed to the UI on a throttled cadence (plus one final update),
    /// instead of one full re-render per sub-directory. Any in-flight run is cancelled
    /// first, e.g. when the user quickly switches folders or navigates away.
    /// </summary>
    internal async Task LoadDirectorySizesInBackgroundAsync()
    {
        // Cancel and replace the previous run (fast folder switches).
        _sizeCts?.Cancel();
        _sizeCts?.Dispose();
        _sizeCts = new CancellationTokenSource();
        var ct = _sizeCts.Token;

        DirectorySizes.Clear();

        if (_fileService is null || CurrentShare is null) return;

        var userContext = await GetCurrentUserContextAsync();
        if (userContext is null) return;

        var dirs = Items.Where(f => f.IsDirectory).ToList();
        if (dirs.Count == 0) return;

        // Throttle timestamp shared across worker threads; guarded with Interlocked
        // so the compare-and-set is race-free and DateTime never tears.
        long lastPushTicks = 0;

        try
        {
            await Parallel.ForEachAsync(
                dirs,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = MaxConcurrentSizeCalculations,
                    CancellationToken = ct
                },
                async (dir, token) =>
                {
                    try
                    {
                        var relativePath = dir.Path;
                        if (relativePath.StartsWith(CurrentShare.Path))
                            relativePath = relativePath[CurrentShare.Path.Length..].TrimStart('/');

                        var size = await _fileService.GetDirectorySizeAsync(relativePath, userContext);
                        DirectorySizes[relativePath] = size;

                        PushThrottledStateChange(ref lastPushTicks);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Could not calculate size for '{Path}'", dir.Path);
                    }
                });
        }
        catch (OperationCanceledException)
        {
            return; // superseded by a newer load — drop this run silently
        }

        // Final update so the last batch of results is rendered even if the
        // throttle window swallowed the trailing push.
        if (!ct.IsCancellationRequested)
            OnStateChanged?.Invoke();
    }

    /// <summary>Raises <see cref="OnStateChanged"/> at most once per throttle window.</summary>
    private void PushThrottledStateChange(ref long lastPushTicks)
    {
        var now = DateTime.UtcNow.Ticks;
        var previous = Interlocked.Read(ref lastPushTicks);

        if (now - previous < SizeUpdateThrottle.Ticks)
            return;

        // Only the thread that wins the swap pushes, so bursts collapse to one update.
        if (Interlocked.CompareExchange(ref lastPushTicks, now, previous) == previous)
            OnStateChanged?.Invoke();
    }

    /// <summary>Returns the computed size if it has already been calculated.</summary>
    public long? GetDirectorySize(FileMetadata dir)
    {
        if (!dir.IsDirectory || CurrentShare is null) return dir.Size;

        var relativePath = dir.Path;
        if (relativePath.StartsWith(CurrentShare.Path))
            relativePath = relativePath[CurrentShare.Path.Length..].TrimStart('/');

        return DirectorySizes.TryGetValue(relativePath, out var size) ? size : null;
    }

    public async Task<(byte[] Data, string ContentType, PreviewKind Kind)?> ReadFileForPreviewAsync(FileMetadata file)
    {
        if (_fileService is null || CurrentShare is null) return null;

        // Never buffer an oversized file into memory — the caller shows a download
        // fallback for anything above the cap.
        if (file.Size > MaxPreviewSizeBytes) return null;

        var userContext = await GetCurrentUserContextAsync();
        if (userContext is null) return null;

        var relativePath = file.Path;
        if (relativePath.StartsWith(CurrentShare.Path))
            relativePath = relativePath[CurrentShare.Path.Length..].TrimStart('/');

        await using var stream = await _fileService.ReadFileAsync(relativePath, userContext);
        var data = await ReadAllBytesAsync(stream);

        var contentType = FileHelper.GetContentType(file.Name);
        var kind = FileHelper.GetPreviewKind(file.Name);

        return (data, contentType, kind);
    }

    /// <summary>Maximum file size that can be previewed inline; larger files download instead.</summary>
    public long GetMaxPreviewSizeBytes() => MaxPreviewSizeBytes;

    /// <summary>
    /// Issues a one-time URL that streams the file straight to the browser via
    /// <c>FileDownloadController</c>, so a download never buffers the file into the
    /// preview first — and files above the inline-preview cap can still be downloaded.
    /// Returns null when no file is addressable or the user is not authenticated;
    /// the controller re-checks ACL access before streaming.
    /// </summary>
    public virtual async Task<string?> GetDownloadUrlAsync(FileMetadata file)
    {
        if (_fileService is null || CurrentShare is null || file.IsDirectory) return null;

        var userContext = await GetCurrentUserContextAsync();
        if (userContext is null) return null;

        if (!ShareRelativePath.TryNormalizeStrict(ToShareRelative(file.Path), out var relative, allowRoot: false))
            return null;

        var ticket = _downloadTickets.Issue(new FileDownloadTicket(
            CurrentShare.Id, userContext.User.Id, relative, file.Name));
        return "/api/files/download?ticket=" + Uri.EscapeDataString(ticket);
    }

    /// <summary>
    /// Builds a download URL for a whole selection: one plain file streams directly, a folder
    /// or a multi-item selection streams as a ZIP via <c>ShareZipDownloadController</c>.
    /// </summary>
    public virtual async Task<string?> GetSelectionDownloadUrlAsync(IReadOnlyList<FileMetadata> items)
    {
        if (_fileService is null || CurrentShare is null || items.Count == 0) return null;

        // A single plain file needs no archive.
        if (items.Count == 1 && !items[0].IsDirectory)
            return await GetDownloadUrlAsync(items[0]);

        var userContext = await GetCurrentUserContextAsync();
        if (userContext is null) return null;

        var relatives = new List<string>();
        foreach (var item in items)
            if (ShareRelativePath.TryNormalizeStrict(ToShareRelative(item.Path), out var rel, allowRoot: false))
                relatives.Add(rel);
        if (relatives.Count == 0) return null;

        var archiveName = items.Count == 1
            ? items[0].Name + ".zip"
            : CurrentShare.Name + ".zip";

        var ticket = _zipTickets.Issue(new ZipDownloadTicket(
            CurrentShare.Id, userContext.User.Id, relatives.ToArray(), archiveName));
        return "/api/files/download-zip?ticket=" + Uri.EscapeDataString(ticket);
    }

    /// <summary>
    /// Hashes a file's content with the requested algorithm, streaming it through the
    /// hasher so files of any size stay off the heap. Returns the lowercase hex digest.
    /// </summary>
    public async Task<string?> ComputeFileHashAsync(
        FileMetadata file, HashAlgorithmName algorithm, CancellationToken cancellationToken = default)
    {
        if (_fileService is null || CurrentShare is null || file.IsDirectory) return null;

        var userContext = await GetCurrentUserContextAsync();
        if (userContext is null) return null;

        await using var stream = await _fileService.ReadFileAsync(ToShareRelative(file.Path), userContext);
        using var hasher = IncrementalHash.CreateHash(algorithm);

        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            hasher.AppendData(buffer, 0, read);

        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }

    // ==================== Versioning ====================

    /// <summary>Strips the share-root prefix, yielding a share-relative path.</summary>
    private string ToShareRelative(string path)
    {
        if (CurrentShare is not null && path.StartsWith(CurrentShare.Path))
            return path[CurrentShare.Path.Length..].TrimStart('/');
        return path;
    }

    /// <summary>All stored versions of a file (newest first), or empty on failure.</summary>
    public async Task<List<FileVersion>> GetFileVersionsAsync(FileMetadata file)
    {
        if (_fileService is null || CurrentShare is null) return new();

        var userContext = await GetCurrentUserContextAsync();
        if (userContext is null) return new();

        try
        {
            return await _fileService.GetFileVersionsAsync(ToShareRelative(file.Path), userContext);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list versions for {Path}", file.Path);
            return new();
        }
    }

    /// <summary>Snapshot timestamps that exist for any file inside a folder (newest first).</summary>
    public async Task<List<DateTime>> GetFolderSnapshotTimestampsAsync(FileMetadata folder)
    {
        if (_fileService is null || CurrentShare is null) return new();

        var userContext = await GetCurrentUserContextAsync();
        if (userContext is null) return new();

        try
        {
            return await _fileService.GetFolderSnapshotTimestampsAsync(ToShareRelative(folder.Path), userContext);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list folder snapshots for {Path}", folder.Path);
            return new();
        }
    }

    /// <summary>The files (with their versions) that made up a folder at a point in time.</summary>
    public async Task<List<FileVersion>> GetFolderSnapshotAsync(FileMetadata folder, DateTime asOfUtc)
    {
        if (_fileService is null || CurrentShare is null) return new();

        var userContext = await GetCurrentUserContextAsync();
        if (userContext is null) return new();

        try
        {
            return await _fileService.GetFolderSnapshotAsync(ToShareRelative(folder.Path), asOfUtc, userContext);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read folder snapshot for {Path}", folder.Path);
            return new();
        }
    }

    /// <summary>
    /// Reads a version's content for inline preview. Returns null when versioning is
    /// unavailable, the version is larger than the preview cap, or the read fails.
    /// </summary>
    public async Task<(byte[] Data, string ContentType, PreviewKind Kind)?> ReadVersionForPreviewAsync(
        string shareRelativePath, DateTime snapshotTimestampUtc, string fileName, long size)
    {
        if (_fileService is null || CurrentShare is null) return null;
        if (size > MaxPreviewSizeBytes) return null;

        var userContext = await GetCurrentUserContextAsync();
        if (userContext is null) return null;

        try
        {
            await using var stream = await _fileService.ReadFileVersionAsync(
                shareRelativePath, snapshotTimestampUtc, userContext);
            var data = await ReadAllBytesAsync(stream);

            return (data, FileHelper.GetContentType(fileName), FileHelper.GetPreviewKind(fileName));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read version {Ts} of {Path}", snapshotTimestampUtc, shareRelativePath);
            return null;
        }
    }

    /// <summary>Reads a version's raw bytes for download, or null on failure.</summary>
    public async Task<byte[]?> ReadVersionBytesAsync(string shareRelativePath, DateTime snapshotTimestampUtc)
    {
        if (_fileService is null || CurrentShare is null) return null;

        var userContext = await GetCurrentUserContextAsync();
        if (userContext is null) return null;

        try
        {
            await using var stream = await _fileService.ReadFileVersionAsync(
                shareRelativePath, snapshotTimestampUtc, userContext);
            return await ReadAllBytesAsync(stream);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read version bytes {Ts} of {Path}", snapshotTimestampUtc,
                shareRelativePath);
            return null;
        }
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream)
    {
        if (stream.Length > int.MaxValue)
            throw new IOException("The file is too large to return as a single byte array.");

        var data = GC.AllocateUninitializedArray<byte>((int)stream.Length);
        stream.Position = 0;
        await stream.ReadExactlyAsync(data);
        return data;
    }

    /// <summary>
    /// Restores a file to an earlier version. The current content is snapshotted
    /// first (via the file service), so the restore is itself reversible.
    /// </summary>
    public async Task<OperationResult> RestoreVersionAsync(FileMetadata file, DateTime snapshotTimestampUtc)
    {
        if (_fileService is null || CurrentShare is null)
            return OperationResult.Fail(Resources.Web_Error_NoShareLoaded);

        var userContext = await GetCurrentUserContextAsync();
        if (userContext is null)
            return OperationResult.Fail(Resources.Web_Error_NotAuthenticated);

        try
        {
            await _fileService.RestoreFileVersionAsync(ToShareRelative(file.Path), snapshotTimestampUtc, userContext);
            _logger.LogInformation("Restored '{Path}' to version {Ts} by {User}",
                file.Path, snapshotTimestampUtc, userContext.User.Username);
            return OperationResult.Ok();
        }
        catch (UnauthorizedAccessException)
        {
            return OperationResult.Fail(Resources.Web_Error_AccessDenied);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore version {Ts} of {Path}", snapshotTimestampUtc, file.Path);
            return OperationResult.Fail(Resources.Web_Version_RestoreFailed);
        }
    }

    /// <summary>Display name relative to a folder prefix (for point-in-time folder listings).</summary>
    public static string RelativeName(string shareRelativeFilePath, string folderShareRelativePath)
    {
        if (!string.IsNullOrEmpty(folderShareRelativePath)
            && shareRelativeFilePath.StartsWith(folderShareRelativePath + "/"))
            return shareRelativeFilePath[(folderShareRelativePath.Length + 1)..];
        return shareRelativeFilePath;
    }

    /// <summary>Public accessor for the share-relative path of a browsed item.</summary>
    public string ShareRelativeOf(FileMetadata item) => ToShareRelative(item.Path);

    // Loads the enabled sync local destinations for the loaded share. Sync config is
    // small (a handful of rows), so an in-memory filter over the admin projection is
    // cheaper than a dedicated query and keeps the repository surface unchanged.
    private async Task LoadShareSyncsAsync(Guid shareId)
    {
        var all = await _syncRepo.GetAllAsync();
        _shareSyncs = all
            .Where(entry => entry.Definition.Enabled && entry.Definition.LocalShareId == shareId)
            .Select(entry => (
                LocalPath: ShareRelativePath.Normalize(entry.Definition.LocalPath),
                entry.Definition.Mode,
                Name: string.IsNullOrWhiteSpace(entry.Definition.DisplayName)
                    ? (entry.Definition.LocalPath.Length == 0 ? CurrentShare?.Name ?? "" : entry.Definition.LocalPath)
                    : entry.Definition.DisplayName,
                entry.Runtime?.LastSuccessfulRunAtUtc,
                // Only pull consults the converged manifest (which paths the remote
                // actually backs); other modes never look at it, so skip the parse.
                RemotePaths: entry.Definition.Mode == SyncMode.Pull
                    ? RemoteBackedPaths(entry.Runtime?.LastSyncManifest)
                    : null,
                entry.Definition.Id))
            .OrderByDescending(sync => sync.LocalPath.Length)
            .ToList();
    }

    // Normalizes a persisted sync manifest into a share-relative, case-insensitive set.
    // Returns null when no manifest has been recorded yet (a pull that has not run under
    // manifest tracking) so the caller falls back to the timestamp heuristic instead of
    // flagging every item as local-only. The manifest is written with the engine's raw
    // "{dir}/{name}" paths (which can carry a leading slash at a root sync), so each entry
    // is renormalized to the canonical form the browser compares against.
    private static HashSet<string>? RemoteBackedPaths(string? manifestJson)
    {
        var manifest = SyncManifest.Deserialize(manifestJson);
        if (manifest is null)
            return null;

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in manifest.Paths)
            paths.Add(ShareRelativePath.Normalize(path));
        return paths;
    }

    /// <summary>
    /// Whether the entry carries a public share link, or lives beneath a shared folder — so the
    /// browser can flag it (and everything under a shared folder) as shared.
    /// </summary>
    public bool IsShared(FileMetadata entry)
    {
        if (_sharedRoots.Count == 0) return false;

        var rel = ShareRelativePath.Normalize(ShareRelativeOf(entry));
        foreach (var (root, _) in _sharedRoots)
        {
            if (string.Equals(rel, root, StringComparison.OrdinalIgnoreCase)) return true;
            if (root.Length == 0 || rel.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <inheritdoc />
    public Guid? GetShareLinkId(FileMetadata entry)
    {
        if (_sharedRoots.Count == 0) return null;

        var rel = ShareRelativePath.Normalize(ShareRelativeOf(entry));
        Guid? best = null;
        var bestLength = -1;
        // Nearest (longest) matching root wins, so an entry with its own link jumps to that
        // link rather than an ancestor's.
        foreach (var (root, linkId) in _sharedRoots)
        {
            var matches = string.Equals(rel, root, StringComparison.OrdinalIgnoreCase)
                          || root.Length == 0
                          || rel.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
            if (matches && root.Length > bestLength)
            {
                best = linkId;
                bestLength = root.Length;
            }
        }
        return best;
    }

    /// <summary>
    /// Returns the sync marker for an entry that is, or lives beneath, a sync's local
    /// destination folder. The most specific (nearest) sync wins when several apply.
    /// </summary>
    public SyncFolderMarker? GetSyncMarker(FileMetadata entry)
    {
        if (_shareSyncs.Count == 0)
            return null;

        var rel = ShareRelativePath.Normalize(ShareRelativeOf(entry));
        foreach (var sync in _shareSyncs)
        {
            // The sync's own destination folder always shows the plain sync emblem: it
            // anchors the sync and must never inherit a warning from a stray child (it is
            // never recorded in its own manifest, and its write time moves with any child).
            if (sync.LocalPath.Length > 0 && rel == sync.LocalPath)
                return new SyncFolderMarker(sync.Mode, sync.Name, SyncItemState.Synced, sync.Id);

            if (sync.LocalPath.Length == 0
                || rel.StartsWith(sync.LocalPath + "/", StringComparison.Ordinal))
            {
                // null (no manifest yet) tells the evaluator to fall back to the
                // timestamp heuristic; a set means membership is authoritative.
                bool? isRemoteBacked = sync.RemotePaths?.Contains(rel);
                var state = SyncItemStateEvaluator.Evaluate(
                    entry.ModifiedAt, sync.LastSuccessfulRunAtUtc, sync.Mode, isRemoteBacked);
                return new SyncFolderMarker(sync.Mode, sync.Name, state, sync.Id);
            }
        }

        return null;
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

    public async Task<OperationResult> ArchiveAsync(List<FileMetadata> items, string format)
    {
        if (_fileService is null || CurrentShare is null)
            return OperationResult.Fail(Resources.Web_Error_NoShareLoaded);

        try
        {
            var userContext = await GetCurrentUserContextAsync();
            if (userContext is null)
                return OperationResult.Fail(Resources.Web_Error_NotAuthenticated);

            var relativePaths = items.Select(item =>
            {
                var path = item.Path;
                if (path.StartsWith(CurrentShare.Path))
                    path = path[CurrentShare.Path.Length..].TrimStart('/');
                return path;
            }).ToList();

            var archiveName = items.Count == 1
                ? Path.GetFileNameWithoutExtension(items[0].Name) + format
                : "archiv" + format;

            var targetPath = GetCurrentPath(archiveName);

            await _fileService.ArchiveAsync(relativePaths, targetPath, format, userContext);

            _logger.LogInformation("Archived {Count} items -> '{Target}' by {User}",
                items.Count, targetPath, userContext.User.Username);

            return OperationResult.Ok();
        }
        catch (UnauthorizedAccessException)
        {
            return OperationResult.Fail(Resources.Web_Error_AccessDenied);
        }
        catch (IOException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult.Fail(string.Format(Resources.Web_Error_ArchiveExists, $"archiv{format}"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error archiving {Count} items", items.Count);
            return OperationResult.Fail(Resources.Web_Error_ArchiveFailed);
        }
    }

    public async Task<OperationResult> UnzipAsync(FileMetadata file)
    {
        if (_fileService is null || CurrentShare is null)
            return OperationResult.Fail(Resources.Web_Error_NoShareLoaded);

        try
        {
            var userContext = await GetCurrentUserContextAsync();
            if (userContext is null)
                return OperationResult.Fail(Resources.Web_Error_NotAuthenticated);

            var relativePath = file.Path;
            if (relativePath.StartsWith(CurrentShare.Path))
                relativePath = relativePath[CurrentShare.Path.Length..].TrimStart('/');

            // Target folder = same directory, named after the zip (without extension)
            var folderName = Path.GetFileNameWithoutExtension(file.Name);
            var parentDir = Kaimo_File_Server.Core.Helpers.ShareRelativePath.GetParent(relativePath);
            var targetDir = Kaimo_File_Server.Core.Helpers.ShareRelativePath.Combine(parentDir, folderName);

            await _fileService.UnzipAsync(relativePath, targetDir, userContext);

            _logger.LogInformation("Unzipped: '{Path}' -> '{Target}' by {User}",
                relativePath, targetDir, userContext.User.Username);

            return OperationResult.Ok();
        }
        catch (UnauthorizedAccessException)
        {
            return OperationResult.Fail(Resources.Web_Error_AccessDenied);
        }
        catch (InvalidDataException)
        {
            return OperationResult.Fail(Resources.Web_Error_NotValidZip);
        }
        catch (IOException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult.Fail(Resources.Web_Error_FolderExists);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error unzipping '{Path}'", file.Path);
            return OperationResult.Fail(Resources.Web_Error_ExtractFailed);
        }
    }

    public async Task<OperationResult> UploadFileAsync(string fileName, Stream fileStream,
        CancellationToken cancellationToken = default)
    {
        if (_fileService is null || CurrentShare is null)
            return OperationResult.Fail(Resources.Web_Error_NoShareLoaded);

        if (string.IsNullOrWhiteSpace(fileName))
            return OperationResult.Fail(Resources.Web_FileName_Missing);

        if (!WindowsFileNameHelper.IsValid(fileName))
        {
            var errors = WindowsFileNameHelper.GetValidationErrors(fileName);
            return OperationResult.Fail(Resources.Web_FileName_InvalidChars + string.Join(", ", errors));
        }

        var userContext = await GetCurrentUserContextAsync();
        if (userContext is null)
            return OperationResult.Fail(Resources.Web_Error_NotAuthenticated);

        var targetPath = GetCurrentPath(fileName);
        
        try
        {
            await _fileService.WriteFileAsync(targetPath, fileStream, userContext, cancellationToken);
            
            _logger.LogInformation("File uploaded: '{Path}' by {User}",
                targetPath, userContext.User.Username);

            return OperationResult.Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Upload failed for '{Path}'", targetPath);

            return ex switch
            {
                OperationCanceledException => OperationResult.Fail(Resources.Web_Upload_Aborted),
                UnauthorizedAccessException => OperationResult.Fail(Resources.Web_Error_AccessDenied),
                IOException ioEx when ioEx.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                    => OperationResult.Fail(Resources.Web_Error_FileExists),
                _ => OperationResult.Fail(Resources.Web_Error_UploadFailed)
            };
        }
    }

    public long GetMaxUploadSizeBytes()
    {
        return MaxUploadSizeBytes;
    }

    // ================= Upload conflict handling (web UI only) =================
    //
    // A web upload whose target name already exists on disk — or collides with another
    // upload in flight in this same session — must not silently overwrite. The incoming
    // bytes are staged to a server-side temp file so the browser stream is freed and the
    // rest of the batch keeps uploading; the collision is then resolved: identical content
    // is dropped, and a genuine conflict waits for the user's Overwrite/Rename/Discard
    // choice. SMB and WebDAV keep their own protocol conflict semantics and never come here.

    public enum UploadConflictAction { Overwrite, Rename, Discard }

    public enum UploadResult { Uploaded, SkippedIdentical, Discarded, Held, Failed }

    /// <summary>A staged upload waiting for the user to decide how to resolve a name collision.</summary>
    public sealed class PendingUploadConflict
    {
        public Guid Id { get; } = Guid.NewGuid();
        public required string FileName { get; init; }
        public required string TargetRelativePath { get; init; }
        public required string StagedTempPath { get; init; }
        public required long IncomingSize { get; init; }
        public required DateTime IncomingModifiedUtc { get; init; }
        // The existing on-disk item at stage time (null when the collision is only with
        // another in-flight upload that has not yet landed on disk).
        public FileMetadata? Existing { get; init; }
        // The share's file service captured at stage time, so resolving the conflict
        // still writes to the originating share even if the (circuit-scoped) view model
        // has since navigated to a different share.
        public required IFileService FileService { get; init; }
    }

    private static readonly string UploadStagingRoot =
        Path.Combine(Path.GetTempPath(), "kaimo-upload-conflicts");

    private readonly object _uploadLock = new();
    private readonly HashSet<string> _reservedUploadPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PendingUploadConflict> _pendingConflicts = new();
    private UploadConflictAction? _stickyConflictAction;
    private int _activeUploadBatches;

    public event Action? OnUploadConflictsChanged;

    public IReadOnlyList<PendingUploadConflict> PendingConflicts
    {
        get { lock (_uploadLock) return _pendingConflicts.ToList(); }
    }

    public bool HasPendingConflicts
    {
        get { lock (_uploadLock) return _pendingConflicts.Count > 0; }
    }

    /// <summary>Marks the start of an upload batch so a "apply to all" choice stays in
    /// effect for the whole run and resets once every batch has finished and drained.</summary>
    public void BeginUploadBatch()
    {
        lock (_uploadLock) _activeUploadBatches++;
    }

    public void EndUploadBatch()
    {
        lock (_uploadLock)
        {
            if (_activeUploadBatches > 0) _activeUploadBatches--;
            ClearStickyIfIdle();
        }
    }

    // Requires _uploadLock.
    private void ClearStickyIfIdle()
    {
        if (_activeUploadBatches == 0 && _pendingConflicts.Count == 0)
            _stickyConflictAction = null;
    }

    /// <summary>
    /// Uploads one selected file, detecting a name collision first. No collision → a
    /// normal upload. Collision → stage the bytes and either skip (identical), apply the
    /// session's sticky choice, or hold it for the user. Always consumes <paramref name="stream"/>.
    /// </summary>
    public async Task<UploadResult> UploadWithConflictHandlingAsync(
        string fileName, Stream stream, CancellationToken ct = default)
    {
        if (_fileService is null || CurrentShare is null || !WindowsFileNameHelper.IsValid(fileName))
            return UploadResult.Failed;

        var user = await GetCurrentUserContextAsync();
        if (user is null) return UploadResult.Failed;

        var target = ShareRelativePath.Normalize(GetCurrentPath(fileName));

        // Probe for an existing item; the read stream doubles as the source for the
        // identical-content check when the caller may read it.
        Stream? existingContent = null;
        bool existsOnDisk;
        bool existingReadable;
        try
        {
            existingContent = await _fileService.ReadFileAsync(target, user);
            existsOnDisk = true;
            existingReadable = true;
        }
        catch (FileNotFoundException) { existsOnDisk = false; existingReadable = false; }
        catch (DirectoryNotFoundException) { existsOnDisk = false; existingReadable = false; }
        catch { existsOnDisk = true; existingReadable = false; } // exists but not readable as a file

        bool reservedByOther;
        lock (_uploadLock)
        {
            reservedByOther = _reservedUploadPaths.Contains(target);
            if (!existsOnDisk && !reservedByOther)
                _reservedUploadPaths.Add(target); // claim it for a straight-through write
        }

        if (!existsOnDisk && !reservedByOther)
        {
            existingContent?.Dispose();
            try
            {
                var r = await UploadFileAsync(fileName, stream, ct);
                return r.Success ? UploadResult.Uploaded : UploadResult.Failed;
            }
            finally
            {
                lock (_uploadLock) _reservedUploadPaths.Remove(target);
            }
        }

        // ---- Collision: stage the incoming bytes and hash them. ----
        Directory.CreateDirectory(UploadStagingRoot);
        var tempPath = Path.Combine(UploadStagingRoot, Guid.NewGuid().ToString("N") + ".upload");
        string incomingHash;
        long incomingSize;
        try
        {
            await using var fs = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await stream.CopyToAsync(fs, ct);
            await fs.FlushAsync(ct);
            fs.Position = 0;
            incomingHash = await ComputeSha256Async(fs);
            incomingSize = fs.Length;
        }
        catch
        {
            existingContent?.Dispose();
            TryDeleteStaged(tempPath);
            throw; // cancellation/IO surfaces to the batch loop
        }

        // Identical content → keep the existing file, drop the copy silently.
        if (existingReadable && existingContent is not null)
        {
            string existingHash;
            await using (existingContent) existingHash = await ComputeSha256Async(existingContent);
            if (string.Equals(existingHash, incomingHash, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteStaged(tempPath);
                return UploadResult.SkippedIdentical;
            }
        }
        else
        {
            existingContent?.Dispose();
        }

        var existingMeta = existsOnDisk ? await SafeGetMetadataAsync(target, user) : null;
        var conflict = new PendingUploadConflict
        {
            FileName = fileName,
            TargetRelativePath = target,
            StagedTempPath = tempPath,
            IncomingSize = incomingSize,
            IncomingModifiedUtc = DateTime.UtcNow,
            Existing = existingMeta,
            FileService = _fileService,
        };

        UploadConflictAction? sticky;
        lock (_uploadLock) sticky = _stickyConflictAction;

        if (sticky is { } act)
        {
            await ApplyConflictActionAsync(conflict, act, user);
            return act == UploadConflictAction.Discard ? UploadResult.Discarded : UploadResult.Uploaded;
        }

        lock (_uploadLock) _pendingConflicts.Add(conflict);
        NotifyConflictsChanged();
        return UploadResult.Held;
    }

    /// <summary>Resolves one held conflict (called from the conflict dialog). With
    /// <paramref name="applyToAll"/> the same choice is applied to every other queued
    /// conflict and remembered for the rest of the upload run.</summary>
    public async Task ResolveConflictAsync(Guid conflictId, UploadConflictAction action, bool applyToAll)
    {
        var user = await GetCurrentUserContextAsync();
        if (user is null) return;

        PendingUploadConflict? conflict;
        List<PendingUploadConflict> alsoApply = new();
        lock (_uploadLock)
        {
            conflict = _pendingConflicts.FirstOrDefault(c => c.Id == conflictId);
            if (conflict is not null) _pendingConflicts.Remove(conflict);
            if (applyToAll)
            {
                _stickyConflictAction = action;
                alsoApply = _pendingConflicts.ToList();
                _pendingConflicts.Clear();
            }
        }

        if (conflict is null) return;

        await ApplyConflictActionAsync(conflict, action, user);
        foreach (var c in alsoApply)
            await ApplyConflictActionAsync(c, action, user);

        lock (_uploadLock) ClearStickyIfIdle();
        NotifyConflictsChanged();
    }

    private async Task ApplyConflictActionAsync(
        PendingUploadConflict c, UploadConflictAction action, UserContext user)
    {
        try
        {
            switch (action)
            {
                case UploadConflictAction.Discard:
                    return;
                case UploadConflictAction.Overwrite:
                    await WriteStagedAsync(c.StagedTempPath, c.TargetRelativePath, user, c.FileService);
                    break;
                case UploadConflictAction.Rename:
                    var renamed = await NextAvailableNameAsync(c.TargetRelativePath, user, c.FileService);
                    await WriteStagedAsync(c.StagedTempPath, renamed, user, c.FileService);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Resolving upload conflict for '{Path}' ({Action}) failed",
                c.TargetRelativePath, action);
        }
        finally
        {
            TryDeleteStaged(c.StagedTempPath);
        }
    }

    private static async Task WriteStagedAsync(
        string tempPath, string targetRelativePath, UserContext user, IFileService fileService)
    {
        await using var fs = new FileStream(
            tempPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await fileService.WriteFileAsync(targetRelativePath, fs, user);
    }

    /// <summary>Windows-Explorer-style "name (2).ext" collision-free variant of a path.</summary>
    private async Task<string> NextAvailableNameAsync(
        string targetRelativePath, UserContext user, IFileService fileService)
    {
        var dir = ShareRelativePath.GetParent(targetRelativePath);
        var name = ShareRelativePath.GetFileName(targetRelativePath);
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);

        for (var n = 2; n < 10000; n++)
        {
            var candidate = Combine(dir, $"{stem} ({n}){ext}");
            if (!await FileExistsAsync(candidate, user, fileService) && Reserve(candidate))
                return candidate;
        }
        return Combine(dir, $"{stem} ({Guid.NewGuid():N}){ext}");

        static string Combine(string folder, string leaf) =>
            ShareRelativePath.Normalize(folder.Length == 0 ? leaf : $"{folder}/{leaf}");
    }

    // Reserves a rename target so two concurrent renames cannot pick the same free name.
    private bool Reserve(string path)
    {
        lock (_uploadLock) return _reservedUploadPaths.Add(path);
    }

    private static async Task<bool> FileExistsAsync(
        string relativePath, UserContext user, IFileService fileService)
    {
        try { await using var s = await fileService.ReadFileAsync(relativePath, user); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch { return true; } // exists but not readable as a file
    }

    private async Task<FileMetadata?> SafeGetMetadataAsync(string relativePath, UserContext user)
    {
        try { return await _fileService!.GetMetadataAsync(relativePath, user); }
        catch { return null; }
    }

    private void NotifyConflictsChanged()
    {
        OnUploadConflictsChanged?.Invoke();
        OnStateChanged?.Invoke();
    }

    private static async Task<string> ComputeSha256Async(Stream stream)
    {
        if (stream.CanSeek) stream.Position = 0;
        using var sha256 = SHA256.Create();
        return Convert.ToHexString(await sha256.ComputeHashAsync(stream));
    }

    private static void TryDeleteStaged(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* a leaked staging temp is cleaned on dispose */ }
    }

    public void Dispose()
    {
        List<string> staged;
        lock (_uploadLock)
        {
            staged = _pendingConflicts.Select(c => c.StagedTempPath).ToList();
            _pendingConflicts.Clear();
        }
        foreach (var path in staged) TryDeleteStaged(path);
    }
}
