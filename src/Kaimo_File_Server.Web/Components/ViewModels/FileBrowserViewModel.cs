using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Security.Claims;
using Kaimo_File_Server.Infrastructure.Search;
using Kaimo_File_Server.Web.Helpers;

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
    
    private readonly IFileServiceFactory _fileServiceFactory;
    private readonly IShareRepository _shareRepo;
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly IUserContextFactory _userContextFactory;
    private readonly AuthenticationStateProvider _authState;
    private readonly ILogger<FileBrowserViewModel> _logger;

    private readonly ISearchService _searchService;

    private IFileService? _fileService;

    public FileBrowserViewModel(
        IFileServiceFactory fileServiceFactory,
        IShareRepository shareRepo,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IUserContextFactory userContextFactory,
        AuthenticationStateProvider authState,
        ILogger<FileBrowserViewModel> logger,
        ISearchService searchService)
    {
        _fileServiceFactory = fileServiceFactory;
        _shareRepo = shareRepo;
        _dbFactory = dbFactory;
        _userContextFactory = userContextFactory;
        _authState = authState;
        _logger = logger;
        _searchService = searchService;
    }

    // -- State --

    public event Action? OnStateChanged;
    public Dictionary<string, long> DirectorySizes { get; private set; } = new();
    private CancellationTokenSource? _sizeCts;
    public Dictionary<string, int> AclCounts { get; private set; } = new();
    public ShareDefinition? CurrentShare { get; private set; }
    public List<FileMetadata> Items { get; private set; } = [];
    public string CurrentPath { get; private set; } = "";
    public bool IsLoading { get; private set; }
    public string? ErrorMessage { get; private set; }

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

            CurrentShare = await _shareRepo.GetByNameAsync(shareName);
            if (CurrentShare is null)
            {
                ErrorMessage = "Share nicht gefunden.";
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

            if (CurrentPath.Contains("..") || CurrentPath.Contains('\0'))
            {
                ErrorMessage = "Ungültiger Pfad.";
                Items = [];
                return;
            }

            var userContext = await GetCurrentUserContextAsync();
            if (userContext is null)
            {
                ErrorMessage = "Nicht authentifiziert.";
                Items = [];
                return;
            }

            _logger.LogDebug("Loading path: '{CurrentPath}' (share={ShareName}, user={User})",
                CurrentPath, shareName, userContext.User.Username);

            Items = await _fileService.ListAsync(CurrentPath, userContext);

            _logger.LogDebug("Found {Total} items ({Dirs} dirs, {Files} files)",
                Items.Count, Directories.Count(), Files.Count());

            _ = LoadDirectorySizesInBackgroundAsync();
        }
        catch (UnauthorizedAccessException)
        {
            ErrorMessage = "Zugriff verweigert.";
            Items = [];
        }
        catch (Exception ex)
        {
            ErrorMessage = "Fehler beim Laden der Dateien.";
            _logger.LogError(ex, "Error loading share {ShareName} path {SubPath}", shareName, subPath);
            Items = [];
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Create a new sub-folder inside the current directory.</summary>
    public async Task<OperationResult> CreateFolderAsync(string folderName)
    {
        if (_fileService is null || CurrentShare is null)
            return OperationResult.Fail("Kein Share geladen.");

        if (string.IsNullOrWhiteSpace(folderName))
            return OperationResult.Fail("Bitte einen Ordnernamen eingeben.");

        // File- / Directoryname Validation
        if (!WindowsFileNameHelper.IsValid(folderName))
        {
            var errors = WindowsFileNameHelper.GetValidationErrors(folderName);
            return OperationResult.Fail("Der Name enthält ungültige Zeichen.  Fehler: " + string.Join(", ", errors));
        }

        try
        {
            var userContext = await GetCurrentUserContextAsync();
            if (userContext is null)
                return OperationResult.Fail("Nicht authentifiziert.");

            var targetPath = string.IsNullOrEmpty(CurrentPath)
                ? folderName
                : $"{CurrentPath}/{folderName}/";

            
            await _fileService.CreateDirectoryAsync(targetPath, userContext);

            _logger.LogInformation("Folder created: '{Path}' by {User}",
                targetPath, userContext.User.Username);

            return OperationResult.Ok();
        }
        catch (UnauthorizedAccessException)
        {
            return OperationResult.Fail("Zugriff verweigert.");
        }
        catch (IOException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult.Fail("Ein Element mit diesem Namen existiert bereits.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating folder '{FolderName}'", folderName);
            return OperationResult.Fail("Fehler beim Erstellen des Ordners.");
        }
    }

    /// <summary>Delete a file or directory (recursively).</summary>
    public async Task<OperationResult> DeleteAsync(FileMetadata item)
    {
        if (_fileService is null || CurrentShare is null)
            return OperationResult.Fail("Kein Share geladen.");

        try
        {
            var userContext = await GetCurrentUserContextAsync();
            if (userContext is null)
                return OperationResult.Fail("Nicht authentifiziert.");

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
            return OperationResult.Fail("Zugriff verweigert.");
        }
        catch (FileNotFoundException)
        {
            return OperationResult.Fail("Datei oder Ordner nicht gefunden.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting '{Path}'", item.Path);
            return OperationResult.Fail("Fehler beim Löschen.");
        }
    }

    /// <summary>Rename a file or directory.</summary>
    public async Task<OperationResult> RenameAsync(FileMetadata item, string newName)
    {
        if (_fileService is null || CurrentShare is null)
            return OperationResult.Fail("Kein Share geladen.");

        if (string.IsNullOrWhiteSpace(newName))
            return OperationResult.Fail("Bitte einen neuen Namen eingeben.");

        if (!WindowsFileNameHelper.IsValid(newName))
        {
            var errors = WindowsFileNameHelper.GetValidationErrors(newName);
            return OperationResult.Fail(
                "Der Name enthält ungültige Zeichen.  Fehler: " + string.Join(", ", errors));
        }

        try
        {
            var userContext = await GetCurrentUserContextAsync();
            if (userContext is null)
                return OperationResult.Fail("Nicht authentifiziert.");

            var relativePath = item.Path;
            if (relativePath.StartsWith(CurrentShare.Path))
                relativePath = relativePath[CurrentShare.Path.Length..].TrimStart('/');

            // BUG FIX: Construct the full sibling path instead of passing
            // just the bare name. FileService.RenameAsync expects a complete
            // share-relative target path, not just a filename.
            var parentDir = Kaimo_File_Server.Core.Helpers.ShareRelativePath.GetParent(relativePath);
            var newRelativePath = Kaimo_File_Server.Core.Helpers.ShareRelativePath.Combine(parentDir, newName);

            await _fileService.RenameAsync(relativePath, newRelativePath, userContext);

            _logger.LogInformation("{Type} renamed: '{OldPath}' -> '{NewPath}' by {User}",
                item.IsDirectory ? "Directory" : "File",
                relativePath, newRelativePath, userContext.User.Username);

            return OperationResult.Ok();
        }
        catch (UnauthorizedAccessException)
        {
            return OperationResult.Fail("Zugriff verweigert.");
        }
        catch (IOException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult.Fail("Ein Element mit diesem Namen existiert bereits.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error renaming '{Path}' to '{NewName}'", item.Path, newName);
            return OperationResult.Fail("Fehler beim Umbenennen.");
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
        if (CurrentShare is null) return;

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

    private async Task LoadDirectorySizesInBackgroundAsync()
    {
        // Vorherigen Lauf abbrechen (z.B. bei schnellem Ordnerwechsel)
        _sizeCts?.Cancel();
        _sizeCts = new CancellationTokenSource();
        var ct = _sizeCts.Token;

        DirectorySizes.Clear();

        if (_fileService is null || CurrentShare is null) return;

        var userContext = await GetCurrentUserContextAsync();
        if (userContext is null) return;

        var dirs = Items.Where(f => f.IsDirectory).ToList();

        foreach (var dir in dirs)
        {
            if (ct.IsCancellationRequested) return;

            try
            {
                var relativePath = dir.Path;
                if (relativePath.StartsWith(CurrentShare.Path))
                    relativePath = relativePath[CurrentShare.Path.Length..].TrimStart('/');

                var size = await _fileService.GetDirectorySizeAsync(relativePath, userContext);

                DirectorySizes[relativePath] = size;
                OnStateChanged?.Invoke(); // UI updaten nach jedem Ordner
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not calculate size for '{Path}'", dir.Path);
            }
        }
    }

    /// <summary>Holt die berechnete Größe, falls schon da.</summary>
    public long? GetDirectorySize(FileMetadata dir)
    {
        if (!dir.IsDirectory || CurrentShare is null) return dir.Size;

        var relativePath = dir.Path;
        if (relativePath.StartsWith(CurrentShare.Path))
            relativePath = relativePath[CurrentShare.Path.Length..].TrimStart('/');

        return DirectorySizes.TryGetValue(relativePath, out var size) ? size : null;
    }
    
    public async Task<(byte[] Data, string ContentType)?> ReadFileForPreviewAsync(FileMetadata file)
    {
        if (_fileService is null || CurrentShare is null) return null;

        var userContext = await GetCurrentUserContextAsync();
        if (userContext is null) return null;

        var relativePath = file.Path;
        if (relativePath.StartsWith(CurrentShare.Path))
            relativePath = relativePath[CurrentShare.Path.Length..].TrimStart('/');

        var stream = await _fileService.ReadFileAsync(relativePath, userContext);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);

        var contentType = FileHelper.GetContentType(file.Name);

        return (ms.ToArray(), contentType);
    }
    
    public async Task<OperationResult> ArchiveAsync(List<FileMetadata> items, string format)
    {
        if (_fileService is null || CurrentShare is null)
            return OperationResult.Fail("Kein Share geladen.");

        try
        {
            var userContext = await GetCurrentUserContextAsync();
            if (userContext is null)
                return OperationResult.Fail("Nicht authentifiziert.");

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

            var targetPath = string.IsNullOrEmpty(CurrentPath)
                ? archiveName
                : $"{CurrentPath}/{archiveName}";

            await _fileService.ArchiveAsync(relativePaths, targetPath, format, userContext);

            _logger.LogInformation("Archived {Count} items -> '{Target}' by {User}",
                items.Count, targetPath, userContext.User.Username);

            return OperationResult.Ok();
        }
        catch (UnauthorizedAccessException)
        {
            return OperationResult.Fail("Zugriff verweigert.");
        }
        catch (IOException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult.Fail($"Ein Archiv mit dem Namen \"archiv{format}\" existiert bereits.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error archiving {Count} items", items.Count);
            return OperationResult.Fail("Fehler beim Archivieren.");
        }
    }
    
    public async Task<OperationResult> UnzipAsync(FileMetadata file)
    {
        if (_fileService is null || CurrentShare is null)
            return OperationResult.Fail("Kein Share geladen.");

        try
        {
            var userContext = await GetCurrentUserContextAsync();
            if (userContext is null)
                return OperationResult.Fail("Nicht authentifiziert.");

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
            return OperationResult.Fail("Zugriff verweigert.");
        }
        catch (InvalidDataException)
        {
            return OperationResult.Fail("Die Datei ist kein gültiges ZIP-Archiv.");
        }
        catch (IOException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult.Fail("Ein Ordner mit diesem Namen existiert bereits.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error unzipping '{Path}'", file.Path);
            return OperationResult.Fail("Fehler beim Entpacken.");
        }
    }

    public async Task<OperationResult> UploadFileAsync(string fileName, Stream fileStream, CancellationToken cancellationToken = default)
    {
        
    if (_fileService is null || CurrentShare is null)
        return OperationResult.Fail("Kein Share geladen.");

    if (string.IsNullOrWhiteSpace(fileName))
        return OperationResult.Fail("Dateiname fehlt.");

    if (!WindowsFileNameHelper.IsValid(fileName))
    {
        var errors = WindowsFileNameHelper.GetValidationErrors(fileName);
        return OperationResult.Fail("Der Dateiname enthält ungültige Zeichen. Fehler: " + string.Join(", ", errors));
    }

    var userContext = await GetCurrentUserContextAsync();
    if (userContext is null)
        return OperationResult.Fail("Nicht authentifiziert.");

    var targetPath = string.IsNullOrEmpty(CurrentPath)
        ? fileName
        : $"{CurrentPath}/{fileName}";

    try
    {
        await _fileService.WriteFileAsync(targetPath, fileStream, userContext, cancellationToken);

        _logger.LogInformation("File uploaded: '{Path}' by {User}",
            targetPath, userContext.User.Username);

        
        var absolutePath = _fileService.ToAbsolutePath(targetPath);
        
        await _searchService.IndexDocumentAsync(new FileDocument
        {
            Id = Guid.NewGuid().ToString(),
            FileName = fileName,
            FilePath = absolutePath,
            Content = "ich mag schuhe",
            FileType = Path.GetExtension(fileName).TrimStart('.'),
            FileSizeBytes = new FileInfo(absolutePath).Length,
            Created = DateTime.UtcNow,
            Modified = DateTime.UtcNow
        });
        
        
        
        
        _logger.LogInformation("File indexed: '{Path}", targetPath);
        
        return OperationResult.Ok();
    }
    catch (Exception ex)
    {
        // Clean up the partial file
        try
        {
            await _fileService.DeleteFileAsync(targetPath, userContext, isRecycleEnabled: false);
            _logger.LogInformation("Cleaned up partial upload: '{Path}'", targetPath);
        }
        catch (Exception cleanupEx)
        {
            _logger.LogWarning(cleanupEx, "Failed to clean up partial upload: '{Path}'", targetPath);
        }

        return ex switch
        {
            OperationCanceledException => OperationResult.Fail("Upload abgebrochen."),
            UnauthorizedAccessException => OperationResult.Fail("Zugriff verweigert."),
            IOException ioEx when ioEx.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                => OperationResult.Fail("Eine Datei mit diesem Namen existiert bereits."),
            _ => OperationResult.Fail("Fehler beim Hochladen.")
        };
    }
    }

    public long GetMaxUploadSizeBytes()
    {
        return MaxUploadSizeBytes;
    }
}