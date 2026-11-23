using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories.Identity;
using Kaimo_File_Server.Core.Repositories.Shares;
using Kaimo_File_Server.Core.Security.Management;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Services.Management;
using Kaimo_File_Server.Infrastructure.Services.Files;
using Kaimo_File_Server.Search;
using Kaimo_File_Server.Web.Components.Search;

namespace Kaimo_File_Server.Web.Services.Files;

/// <summary>Home-folder hits of a search and the labels that show other homes by username.</summary>
public sealed record HomeSearchResult(List<FileDocument> Hits, HomeLabels Labels)
{
    public static HomeSearchResult Empty { get; } = new([], HomeLabels.None);
}

/// <summary>
/// Finds other users' home folders by their owner's username. Home folders are named by the
/// owner's GUID, which nobody searches for: a search over all shares or over the root of the
/// home-folder share also lists every home whose owner's username contains the term, as a folder
/// hit named like the user. The candidates are exactly the folders the file browser lists at the
/// root of the share (the same ACL-filtered listing, behind the same
/// <see cref="ManagementPermission.AccessHomeContents"/> gate), so every hit opens. The caller's
/// own home is reached as "user" and left out. Any scope that reaches the share also gets
/// <see cref="HomeLabels"/>, so paths inside a home show the username like the browser does.
/// </summary>
public sealed class HomeFolderSearch(
    IUserRepository users, IShareRepository shares, IFileServiceFactory files, IManagementAuthService auth)
{
    /// <summary>At most this many home hits per search.</summary>
    public const int MaxHits = 5;

    private const string IdPrefix = "home:";

    public async Task<HomeSearchResult> FindAsync(string query, UserContext user,
        IEnumerable<(string? Share, string Sub)> targets)
    {
        var scopes = targets.ToList();
        if (!scopes.Any(ReachesHomes))
            return HomeSearchResult.Empty;
        var share = await shares.GetByNameAsync(HomeDirectoryService.ShareName);
        if (share is not { IsUserHomes: true, IsEnabled: true })
            return HomeSearchResult.Empty;

        // ponytail: loads all users per search; move to PrincipalDirectoryCache if user counts grow large.
        var owners = (await users.GetAllAsync())
            .ToDictionary(u => u.Id.ToString("D"), u => u.Username, StringComparer.OrdinalIgnoreCase);
        var labels = new HomeLabels(owners);

        query = query.Trim();
        if (query.Length == 0 || !scopes.Any(CoversHomesRoot)
            || !await auth.HasAnyPermissionAsync(user, ManagementPermission.AccessHomeContents))
            return new([], labels);

        List<Core.Domain.FileMetadata> homes;
        try { homes = await files.CreateForShare(share).ListAsync("", user); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return new([], labels);
        }

        var ownId = user.User.Id.ToString("D");
        var hits = homes
            .Where(h => h.IsDirectory && !h.Name.Equals(ownId, StringComparison.OrdinalIgnoreCase))
            .Select(h => (Folder: h, Owner: owners.GetValueOrDefault(h.Name)))
            .Where(h => h.Owner is not null && h.Owner.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(h => h.Owner, StringComparer.OrdinalIgnoreCase)
            .Take(MaxHits)
            .Select(h => new FileDocument
            {
                Id = IdPrefix + h.Folder.Name,
                FileName = h.Owner!,
                ShareName = share.Name,
                AbsolutePath = "",
                SharePath = h.Folder.Name,
                FileType = "folder",
                IsDirectory = true,
                Created = h.Folder.CreatedAt,
                Modified = h.Folder.ModifiedAt,
            })
            .ToList();
        return new(hits, labels);
    }

    // All shares, or anywhere in the home-folder share (labels are needed inside a home too).
    private static bool ReachesHomes((string? Share, string Sub) target)
        => target.Share is null
           || target.Share.Equals(HomeDirectoryService.ShareName, StringComparison.OrdinalIgnoreCase);

    // All shares, or the home-folder share at its root (a search inside a home finds no homes).
    private static bool CoversHomesRoot((string? Share, string Sub) target)
        => target.Share is null || (ReachesHomes(target) && target.Sub.Trim('/').Length == 0);
}
