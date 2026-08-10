using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Web.Helpers;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>
/// Reusable starting point for Cloud Access view models. It owns only browser
/// state and navigation semantics; provider authentication and remote I/O stay in
/// the concrete OneDrive, SMB, NFS, or other implementation.
/// </summary>
public abstract class RemoteFileBrowserViewModelBase : IFileBrowserViewModel
{
    protected RemoteFileBrowserViewModelBase(BrowserCapabilities capabilities)
    {
        Capabilities = capabilities;
    }

    public event Action? OnStateChanged;

    public BrowserCapabilities Capabilities { get; protected set; }
    public BrowserShareInfo? CurrentBrowserShare { get; protected set; }
    public List<FileMetadata> Items { get; protected set; } = [];
    public string CurrentPath { get; protected set; } = "";
    public bool IsLoading { get; protected set; }
    public string? ErrorMessage { get; protected set; }
    public bool CanManageAcls => false;
    public bool CanManageSyncs => false;

    public IEnumerable<FileMetadata> Directories
        => Items.Where(item => item.IsDirectory).OrderBy(item => item.Name);

    public IEnumerable<FileMetadata> Files
        => Items.Where(item => !item.IsDirectory).OrderBy(item => item.Name);

    public bool HasParent => !string.IsNullOrEmpty(CurrentPath);

    public string ParentPath => ShareRelativePath.GetParent(CurrentPath);

    public List<(string Name, string FullPath)> Breadcrumbs
    {
        get
        {
            if (string.IsNullOrEmpty(CurrentPath))
                return [];

            var parts = CurrentPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var result = new List<(string Name, string FullPath)>(parts.Length);
            for (var index = 0; index < parts.Length; index++)
                result.Add((parts[index], string.Join('/', parts[..(index + 1)])));
            return result;
        }
    }

    public abstract Task LoadShareAsync(string shareKey, string subPath = "");
    public abstract Task RefreshCurrentDirectoryAsync();
    public abstract Task<OperationResult> CreateFolderAsync(string folderName);
    public abstract Task CreateFolderAtAsync(string path);
    public abstract Task<OperationResult> DeleteAsync(FileMetadata item);
    public abstract Task<OperationResult> RenameAsync(FileMetadata item, string newName);
    public abstract Task<OperationResult> MoveAsync(FileMetadata item, string destinationPath);
    public abstract Task<List<FileMetadata>> ListDirectoryAsync(string directoryPath);
    public abstract Task CopyAsync(
        FileMetadata item,
        string targetPath,
        CancellationToken cancellationToken);
    public abstract Task<(byte[] Data, string ContentType, PreviewKind Kind)?>
        ReadFileForPreviewAsync(FileMetadata file);
    public abstract Task<OperationResult> UploadFileAsync(
        string fileName,
        Stream fileStream,
        CancellationToken cancellationToken = default);

    public virtual long GetMaxPreviewSizeBytes() => 25L * 1024 * 1024;
    public virtual long GetMaxUploadSizeBytes() => 1100L * 1024 * 1024;
    public virtual Task<long?> CalculateDirectorySizeAsync(
        FileMetadata directory, CancellationToken cancellationToken = default)
        => Task.FromResult<long?>(null);

    public virtual string ShareRelativeOf(FileMetadata item)
        => ShareRelativePath.Normalize(item.Path);

    protected void BeginLoad()
    {
        IsLoading = true;
        ErrorMessage = null;
        NotifyStateChanged();
    }

    protected void CompleteLoad(
        BrowserShareInfo share,
        string currentPath,
        IEnumerable<FileMetadata> items)
    {
        CurrentBrowserShare = share with { Kind = BrowserShareKind.Remote };
        CurrentPath = ShareRelativePath.Normalize(currentPath);
        Items = items.ToList();
        IsLoading = false;
        ErrorMessage = null;
        NotifyStateChanged();
    }

    protected void FailLoad(string message)
    {
        Items = [];
        IsLoading = false;
        ErrorMessage = message;
        NotifyStateChanged();
    }

    protected void NotifyStateChanged() => OnStateChanged?.Invoke();
}
