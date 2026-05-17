using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Storage;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public class FileBrowserViewModel
{
    private readonly IStorageEngine _storage;
    private readonly IShareRepository _shareRepo;
    private readonly ILogger<FileBrowserViewModel> _logger;

    public FileBrowserViewModel(
        IStorageEngine storage,
        IShareRepository shareRepo,
        ILogger<FileBrowserViewModel> logger)
    {
        _storage = storage;
        _shareRepo = shareRepo;
        _logger = logger;
    }

    // ── State ──

    public ShareDefinition? CurrentShare { get; private set; }
    public List<FileMetadata> Items { get; private set; } = [];
    public string CurrentPath { get; private set; } = "";
    public bool IsLoading { get; private set; }
    public string? ErrorMessage { get; private set; }

    // ── Computed ──

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

    // ── Commands ──

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

            // FIX: CurrentShare.Path statt CurrentShare.Name verwenden
            var sharePath = CurrentShare.Path.Trim('/');

            // subPath bereinigen und Share-Prefix entfernen,
            // falls das UI ihn mitschickt
            var cleanSub = (subPath ?? "").Trim('/');
            if (cleanSub.Equals(sharePath, StringComparison.OrdinalIgnoreCase))
                cleanSub = "";
            else if (cleanSub.StartsWith(sharePath + "/", StringComparison.OrdinalIgnoreCase))
                cleanSub = cleanSub[(sharePath.Length + 1)..];

            CurrentPath = cleanSub;

            var storagePath = string.IsNullOrEmpty(CurrentPath)
                ? sharePath
                : $"{sharePath}/{CurrentPath}";

            // Path-Traversal und gefährliche Zeichen verhindern
            if (storagePath.Contains("..") || storagePath.Contains('\0'))
            {
                ErrorMessage = "Ungültiger Pfad.";
                Items = [];
                return;
            }

            _logger.LogDebug("Loading path: '{StoragePath}' (share={ShareName}, sub={SubPath})",
                storagePath, shareName, CurrentPath);

            Items = await _storage.ListAsync(storagePath);

            _logger.LogDebug("Found {Total} items ({Dirs} dirs, {Files} files)",
                Items.Count, Directories.Count(), Files.Count());
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
}