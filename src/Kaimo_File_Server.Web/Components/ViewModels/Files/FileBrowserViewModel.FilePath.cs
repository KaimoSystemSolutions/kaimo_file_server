using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>
/// A path that names a file (a path pasted from Explorer, a deep link) opens the file's folder,
/// like Explorer does, and <see cref="FileToSelect"/> tells the view which entry to select.
/// </summary>
public partial class FileBrowserViewModel
{
    /// <inheritdoc />
    public string? FileToSelect { get; private set; }

    // Lists CurrentPath; when it is a visible file instead of a folder, lists its folder.
    // Anything else (missing, hidden, denied) keeps the original error, so this reveals nothing
    // the parent listing would not show anyway (FindEntryAsync applies the same ACL rules).
    private async Task<List<FileMetadata>> ListOrOpenFileAsync(UserContext user)
    {
        var files = _fileService!;
        try
        {
            return await files.ListAsync(CurrentPath, user);
        }
        catch (Exception ex) when ((ex is DirectoryNotFoundException or UnauthorizedAccessException)
                                   && CurrentPath.Length > 0)
        {
            FileMetadata? entry;
            try { entry = await files.FindEntryAsync(CurrentPath, user); }
            catch (Exception lookup) when (lookup is DirectoryNotFoundException or UnauthorizedAccessException) { entry = null; }
            if (entry is not { IsDirectory: false })
                throw;

            CurrentPath = ShareRelativePath.GetParent(CurrentPath);
            FileToSelect = entry.Name;
            return await files.ListAsync(CurrentPath, user);
        }
    }
}
