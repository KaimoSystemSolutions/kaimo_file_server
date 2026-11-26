using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Infrastructure.Backup.Restic;
using Kaimo_File_Server.Infrastructure.Services.Files;
using Kaimo_File_Server.Web.Components.Search;

namespace Kaimo_File_Server.Web.Components.ViewModels.Backup;

public partial class BackupViewModel
{
    // ══════════════════════════════════════════
    //  Home folders shown by username (restore tab and activity log)
    // ══════════════════════════════════════════

    /// <summary>Owner usernames of the home folders; loaded only when a view of the home-folder share needs them.</summary>
    private HomeLabels _homeLabels = HomeLabels.None;

    // Shown paths of the selected share; cleared whenever the labels are reloaded. The changes
    // view filters and renders thousands of paths on every keystroke.
    private readonly Dictionary<string, string> _shownPaths = new(StringComparer.Ordinal);

    // Restore runs whose homes the loaded labels cover; a new one triggers a reload.
    private readonly HashSet<Guid> _labelledRestoreRuns = [];

    private bool InHomesShare => RestoreShare?.IsUserHomes == true;

    /// <summary>
    /// A share-relative path of the selected share as shown: on the home-folder share its first
    /// segment (a home named by its owner's GUID) as the owner's username, like the file browser.
    /// Navigation and restore requests keep the real path.
    /// </summary>
    public string DisplayPath(string sharePath)
    {
        if (!InHomesShare)
            return sharePath;
        if (!_shownPaths.TryGetValue(sharePath, out var shown))
            _shownPaths[sharePath] = shown = _homeLabels.DisplayPath(sharePath);
        return shown;
    }

    /// <summary>
    /// The real path for a typed one (path bar): on the home-folder share a username in the first
    /// segment is swapped for that user's home folder, as the file browser accepts it.
    /// </summary>
    private string ResolveTypedPath(string folder) => InHomesShare ? _homeLabels.ResolvePath(folder) : folder;

    /// <summary>At the root of the home-folder share the homes are ordered by their shown name, not their GUID.</summary>
    private IReadOnlyList<BackupBrowseEntry> OrderByLabel(IReadOnlyList<BackupBrowseEntry> entries)
        => InHomesShare && BrowseFolder.Length == 0
            ? entries.OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => EntryLabel(e.Name), StringComparer.CurrentCultureIgnoreCase).ToList()
            : entries;

    /// <summary>Name shown for an entry of <see cref="BrowseFolder"/>: only the share root holds homes.</summary>
    public string EntryLabel(string name) => BrowseFolder.Length == 0 ? DisplayPath(name) : name;

    /// <summary>The folder a restore run read from, a home shown by its owner's username.</summary>
    public string RestoreSourceFolder(BackupRestoreDetails restore)
        => FromHomes(restore) ? _homeLabels.DisplayPath(restore.Request.FolderPath) : restore.Request.FolderPath;

    /// <summary>The entries a restore run picked; at the root of the home-folder share these are homes.</summary>
    public IReadOnlyList<string> RestoreItems(BackupRestoreDetails restore)
        => FromHomes(restore) && restore.Request.FolderPath.Trim('/').Length == 0
            ? restore.Request.Items.Select(_homeLabels.DisplayPath).ToList()
            : restore.Request.Items;

    /// <summary>The folder a restore run wrote to, a home shown by its owner's username.</summary>
    public string RestoreTargetFolder(BackupRestoreOutcome result)
        => IsHomesShareName(result.DestinationShareName) ? _homeLabels.DisplayPath(result.DestinationPath) : result.DestinationPath;

    // The source share name is recorded once the run started; a queued run is matched by its share.
    // A share the viewer cannot restore (e.g. a repository admin without restore rights) is unknown
    // here: it is treated as the home-folder share, which is safe because only home folders are
    // named after a user id, and a path naming no user is never relabelled.
    private bool FromHomes(BackupRestoreDetails restore)
        => restore.Result?.SourceShareName is { } name
            ? IsHomesShareName(name)
            : RestoreShares.FirstOrDefault(s => s.ShareId == restore.Request.ShareId) is not { } share || share.IsUserHomes;

    private static bool IsHomesShareName(string? name)
        => string.Equals(name, HomeDirectoryService.ShareName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Loads the owner names when the home-folder share is selected (again on every selection, so new users show up).</summary>
    private Task LoadHomeLabelsAsync() => InHomesShare ? ReloadHomeLabelsAsync() : Task.CompletedTask;

    /// <summary>
    /// The activity log names restore sources and targets: loads the owner names when it lists a
    /// restore run the loaded names do not cover yet, not on every reload of the page.
    /// </summary>
    private async Task LoadActivityHomeLabelsAsync()
    {
        var restores = Runs.Where(r => r.Type == BackupRunType.Restore).Select(r => r.Id).ToList();
        if (restores.All(_labelledRestoreRuns.Contains))
            return;
        await ReloadHomeLabelsAsync();
        _labelledRestoreRuns.UnionWith(restores);
    }

    private async Task ReloadHomeLabelsAsync()
    {
        _homeLabels = HomeLabels.FromUsers(await users.GetAllAsync());
        _shownPaths.Clear();
    }
}
