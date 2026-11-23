using Kaimo_File_Server.Infrastructure.Services.Files;
using Kaimo_File_Server.Search;

namespace Kaimo_File_Server.Web.Components.Search;

/// <summary>
/// Shows search hits in other users' homes the way the file browser does: the home folder
/// (named by the owner's GUID) as the owner's username. Only the shown text changes; the hit's
/// <see cref="FileDocument.SharePath"/> keeps the GUID for navigation.
/// </summary>
public sealed class HomeLabels(IReadOnlyDictionary<string, string> ownerNames)
{
    /// <summary>No labels: every path is shown as it is.</summary>
    public static readonly HomeLabels None = new(new Dictionary<string, string>());

    /// <summary>The hit's share-relative path as shown (see the class remarks).</summary>
    public string DisplayPath(FileDocument hit)
    {
        if (!hit.ShareName.Equals(HomeDirectoryService.ShareName, StringComparison.OrdinalIgnoreCase))
            return hit.SharePath;

        var path = hit.SharePath.Replace('\\', '/').Trim('/');
        var slash = path.IndexOf('/');
        var home = slash < 0 ? path : path[..slash];
        return ownerNames.TryGetValue(home, out var owner) ? owner + path[home.Length..] : hit.SharePath;
    }
}
