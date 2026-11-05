using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Web.Services;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>
/// Virtual shares mounted as folders. They exist only in the database, so the listing gets
/// one synthetic folder entry per mount the user may open. The entry only navigates (the
/// page then shows the remote browser); every recursive host-side operation (size, ZIP,
/// copy) skips it, because the remote content is not part of the host share.
/// </summary>
public partial class FileBrowserViewModel
{
    // Null for derived browsers (public links, homes), so they never show mounts.
    private readonly CloudAccessMountResolver? _mountResolver;
    private Dictionary<string, MountedFolder> _mountEntries = new(StringComparer.OrdinalIgnoreCase);

    public bool IsMountEntry(FileMetadata entry) => _mountEntries.ContainsKey(entry.Path);

    public MountedFolder? MountOf(FileMetadata entry) => _mountEntries.GetValueOrDefault(entry.Path);

    /// <summary>Items without mount entries: what host-side batch operations may act on.</summary>
    private List<FileMetadata> WithoutMountEntries(IEnumerable<FileMetadata> items)
        => items.Where(item => !IsMountEntry(item)).ToList();

    private async Task InjectMountEntriesAsync(UserContext userContext)
    {
        _mountEntries = new(StringComparer.OrdinalIgnoreCase);
        if (_mountResolver is null || CurrentShare is null) return;

        await _mountResolver.RefreshAsync(CurrentShare, userContext);
        var mounts = _mountResolver.ChildrenOf(CurrentShare.Name, CurrentPath);
        if (mounts.Count == 0) return;

        var root = CurrentShare.Path.TrimEnd('/');
        var names = mounts.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // A real folder that won the creation race is shadowed by the mount (see the guard).
        var items = Items.Where(item => !names.Contains(item.Name)).ToList();
        foreach (var mount in mounts)
        {
            var entry = new FileMetadata
            {
                ShareId = CurrentShare.Id,
                Path = $"{root}/{mount.MountPath}",
                Name = mount.Name,
                IsDirectory = true,
            };
            items.Add(entry);
            _mountEntries[entry.Path] = mount;
        }
        Items = items;
    }
}
