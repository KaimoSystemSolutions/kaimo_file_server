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

    /// <summary>
    /// Breadcrumb-Segmente: "docs/reports/2026" → ["docs", "docs/reports", "docs/reports/2026"]
    /// </summary>
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
            CurrentPath = subPath.Trim('/');

            CurrentShare = await _shareRepo.GetByNameAsync(shareName);
            if (CurrentShare is null)
            {
                ErrorMessage = "Share nicht gefunden.";
                Items = [];
                return;
            }

            // Share.Path + SubPath = voller Pfad im Storage
            var storagePath = string.IsNullOrEmpty(CurrentPath)
                ? CurrentShare.Path
                : $"{CurrentShare.Path.TrimEnd('/')}/{CurrentPath}";

            // ListAsync nutzt den relativen Pfad ab Storage-Root
            Items = await _storage.ListAsync(storagePath);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Fehler: {ex.Message}";
            Items = [];
        }
        finally
        {
            IsLoading = false;
        }
    }
}
