using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;
using System.Security.Claims;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public class FileBrowserViewModel
{
    private readonly IFileServiceFactory _fileServiceFactory;
    private readonly IShareRepository _shareRepo;
    private readonly IUserContextFactory _userContextFactory;
    private readonly AuthenticationStateProvider _authState;
    private readonly ILogger<FileBrowserViewModel> _logger;

    private IFileService? _fileService;

    public FileBrowserViewModel(
        IFileServiceFactory fileServiceFactory,
        IShareRepository shareRepo,
        IUserContextFactory userContextFactory,
        AuthenticationStateProvider authState,
        ILogger<FileBrowserViewModel> logger)
    {
        _fileServiceFactory = fileServiceFactory;
        _shareRepo = shareRepo;
        _userContextFactory = userContextFactory;
        _authState = authState;
        _logger = logger;
    }

    // -- State --

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

            // Pro Share einen eigenen FileService mit ACL-Prüfung
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

            // ★ Aktuellen User für ACL-gefiltertes Listing auflösen
            var userContext = await GetCurrentUserContextAsync();
            if (userContext is null)
            {
                ErrorMessage = "Nicht authentifiziert.";
                Items = [];
                return;
            }

            _logger.LogDebug("Loading path: '{CurrentPath}' (share={ShareName}, user={User})",
                CurrentPath, shareName, userContext.User.Username);

            // ★ ACL-gefiltertes Listing
            Items = await _fileService.ListAsync(CurrentPath, userContext);

            _logger.LogDebug("Found {Total} items ({Dirs} dirs, {Files} files)",
                Items.Count, Directories.Count(), Files.Count());
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

    private async Task<UserContext?> GetCurrentUserContextAsync()
    {
        var state = await _authState.GetAuthenticationStateAsync();
        var username = state.User.Identity?.Name;
        if (string.IsNullOrEmpty(username)) return null;
        return await _userContextFactory.CreateByUsernameAsync(username);
    }
}