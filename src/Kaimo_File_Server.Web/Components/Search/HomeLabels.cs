using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Infrastructure.Services.Files;
using Kaimo_File_Server.Search;

namespace Kaimo_File_Server.Web.Components.Search;

/// <summary>
/// Shows paths in other users' homes the way the file browser does: the home folder (named by
/// the owner's GUID) as the owner's username. Used for search hits and for other views of the
/// home-folder share (backup restore, changes and activity, client devices). Only the shown text
/// changes; the real path (e.g. <see cref="FileDocument.SharePath"/>) keeps the GUID for
/// navigation, and <see cref="ResolvePath"/> maps a typed username back to it.
/// </summary>
public sealed class HomeLabels(IReadOnlyDictionary<string, string> ownerNames)
{
    /// <summary>No labels: every path is shown as it is.</summary>
    public static readonly HomeLabels None = new(new Dictionary<string, string>());

    /// <summary>Labels for the homes of <paramref name="users"/> (home folder = user id, "D" format).</summary>
    public static HomeLabels FromUsers(IEnumerable<User> users)
        => new(users.ToDictionary(u => u.Id.ToString("D"), u => u.Username, StringComparer.OrdinalIgnoreCase));

    /// <summary>The hit's share-relative path as shown (see the class remarks).</summary>
    public string DisplayPath(FileDocument hit)
        => hit.ShareName.Equals(HomeDirectoryService.ShareName, StringComparison.OrdinalIgnoreCase)
            ? Relabel(hit.SharePath) ?? hit.SharePath
            : hit.SharePath;

    /// <summary>
    /// A path relative to the home-folder share as shown: its first segment as the owner's
    /// username. A path whose first segment names no user is returned unchanged.
    /// </summary>
    public string DisplayPath(string homesSharePath) => Relabel(homesSharePath) ?? homesSharePath;

    /// <summary>
    /// The real path for a typed one: a username in the first segment is swapped for that user's
    /// home folder, as the file browser accepts it. A path whose first segment already is a home
    /// folder, or names no user, is returned unchanged.
    /// </summary>
    public string ResolvePath(string shownPath)
    {
        var path = shownPath.Replace('\\', '/').Trim('/');
        var first = FirstSegment(path);
        if (first.Length == 0 || ownerNames.ContainsKey(first))
            return shownPath;
        return HomesByOwner.TryGetValue(first, out var home) ? home + path[first.Length..] : shownPath;
    }

    /// <summary>The first segment of a share-relative path (the home it lies in on the home-folder share).</summary>
    public static string FirstSegment(string sharePath)
    {
        var path = sharePath.Replace('\\', '/').Trim('/');
        var slash = path.IndexOf('/');
        return slash < 0 ? path : path[..slash];
    }

    // Owner username → home folder; built on the first typed path (usernames are unique, first wins).
    private Dictionary<string, string>? _homesByOwner;

    private Dictionary<string, string> HomesByOwner => _homesByOwner ??= BuildHomesByOwner();

    private Dictionary<string, string> BuildHomesByOwner()
    {
        var byOwner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (home, owner) in ownerNames)
            byOwner.TryAdd(owner, home);
        return byOwner;
    }

    // The normalized path with the home segment replaced; null when no user owns that segment.
    private string? Relabel(string sharePath)
    {
        var path = sharePath.Replace('\\', '/').Trim('/');
        var home = FirstSegment(path);
        return ownerNames.TryGetValue(home, out var owner) ? owner + path[home.Length..] : null;
    }
}
