using System.Collections.Concurrent;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Security.Claims;
using Kaimo_File_Server.Web.Helpers;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Search;
using Kaimo_File_Server.Core.Language;
using System.Diagnostics;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>Simple result wrapper for UI operations.</summary>
public record OperationResult(bool Success, string? Error = null)
{
    public static OperationResult Ok() => new(true);
    public static OperationResult Fail(string error) => new(false, error);
}

public class FileBrowserViewModel
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

    private readonly ISearchService _searchService;

    private IFileService? _fileService;

    public FileBrowserViewModel(
        IFileServiceFactory fileServiceFactory,
        IShareRepository shareRepo,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IUserContextFactory userContextFactory,
        IManagementAuthService mgmtAuth,
        AuthenticationStateProvider authState,
        ILogger<FileBrowserViewModel> logger,
        ISearchService searchService,
        IUserRepository userRepo)
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
    }

    // -- State --

    public event Action? OnStateChanged;

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

    // -- Computed --

    public IEnumerable<FileMetadata> Directories
        => Items.Where(f => f.IsDirectory).OrderBy(f => f.Name);

    public IEnumerable<FileMetadata> Files
        => Items.Where(f => !f.IsDirectory).OrderBy(f => f.Name);

    public bool HasParent => !string.IsNullOrEmpty(CurrentPath);

    public string ParentPath
    {
        get
        {
            if (string.IsNullOrEmpty(CurrentPath)) return "";
            var lastSlash = CurrentPath.LastIndexOf('/');
            return lastSlash <= 0 ? "" : CurrentPath[..lastSlash];
        }
    }

    public List<(string Name, string FullPath)> Breadcrumbs
    {
        get
        {
            if (string.IsNullOrEmpty(CurrentPath)) return [];
            var parts = CurrentPath.Split('/');
            var result = new List<(string, string)>();
            for (int i = 0; i < parts.Length; i++)
            {
                result.Add((parts[i], string.Join('/', parts[..(i + 1)])));
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
            _fileService = null;

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

            CurrentPath = cleanSub;

            if (!ShareRelativePath.IsValid(CurrentPath))
            {
                ErrorMessage = Resources.Web_Error_InvalidPath;
                Items = [];
                return;
            }

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
            Items = [];
        }
        catch (UnauthorizedAccessException)
        {
            ErrorMessage = Resources.Web_Error_AccessDenied;
            CanManageAcls = false;
            Items = [];
        }
        catch (Exception ex)
        {
            ErrorMessage = Resources.Web_Error_LoadFilesFailed;
            CanManageAcls = false;
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
        await _fileService.CreateDirectoryAsync(path, await GetCurrentUserContextAsync());
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
        var userContext = await GetCurrentUserContextAsync();
        return await _fileService.ListAsync(dirPath, userContext);
    }
    
    public async Task CopyAsync(FileMetadata item, string targetPath, CancellationToken cancellationToken)
    {
        var userContext = await GetCurrentUserContextAsync();
        Stream fileToCopy = await _fileService.ReadFileAsync(item.Path, userContext);
        await _fileService.WriteFileAsync(targetPath, fileToCopy, userContext, cancellationToken);
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

    private async Task<UserContext?> GetCurrentUserContextAsync()
    {
        var state = await _authState.GetAuthenticationStateAsync();
        var username = state.User.Identity?.Name;
        if (string.IsNullOrEmpty(username)) return null;
        return await _userContextFactory.CreateByUsernameAsync(username);
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
}
