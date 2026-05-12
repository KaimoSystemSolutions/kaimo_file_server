using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Storage;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public class FileBrowserViewModel
{
    private readonly IStorageEngine _storage;
    private readonly IShareRepository _shareRepo;

    public FileBrowserViewModel(IStorageEngine storage, IShareRepository shareRepo)
    {
        _storage = storage;
        _shareRepo = shareRepo;
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

            var sharePath = CurrentShare.Name.Trim('/');

            // subPath bereinigen und Share-Prefix entfernen,
            // falls das UI ihn mitschickt (z.B. "projekte/hallo" statt "hallo")
            var cleanSub = (subPath ?? "").Trim('/');
            if (cleanSub.Equals(sharePath, StringComparison.OrdinalIgnoreCase))
                cleanSub = "";
            else if (cleanSub.StartsWith(sharePath + "/", StringComparison.OrdinalIgnoreCase))
                cleanSub = cleanSub[(sharePath.Length + 1)..];

            CurrentPath = cleanSub;

            var storagePath = string.IsNullOrEmpty(CurrentPath)
                ? sharePath
                : $"{sharePath}/{CurrentPath}";

            // Path-Traversal verhindern (z.B. "../../etc")
            if (storagePath.Contains(".."))
            {
                ErrorMessage = "Ungültiger Pfad.";
                Items = [];
                return;
            }

            Console.WriteLine($"[FileBrowser] Loading path: '{storagePath}' (share={shareName}, sub={CurrentPath})");

            Items = await _storage.ListAsync(storagePath);

            Console.WriteLine($"[FileBrowser] Found {Items.Count} items ({Directories.Count()} dirs, {Files.Count()} files)");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Fehler: {ex.Message}";
            Console.WriteLine($"[FileBrowser] Error: {ex}");
            Items = [];
        }
        finally
        {
            IsLoading = false;
        }
    }
}
