using System.Text.Json;
using Kaimo_File_Server.Infrastructure.Services.Files;
using Kaimo_File_Server.Search;
using Kaimo_File_Server.Web.Helpers;

namespace Kaimo_File_Server.Web.Components.Search;

/// <summary>A searched location as the web UI names it: share (null = all shares) and folder.</summary>
public sealed record SearchResultsTarget(string? Share, string Path);

/// <summary>
/// What the results page (/search) searches. It travels in the browser history entry
/// (<c>NavigationOptions.HistoryEntryState</c>), not in the URL: the search term never lands in
/// URLs, server logs or bookmarks, and back/forward restore each search. The own home is named
/// "user" with a home-relative path, so its folder GUID never leaves the server. The state is
/// client-controlled like any request; the page searches through <c>ISearchService</c>, which
/// enforces the ACLs, so a forged state cannot widen what a user sees.
/// </summary>
public sealed record SearchResultsRequest(
    string Query, IReadOnlyList<SearchResultsTarget> Targets, bool IncludeRecycleBin,
    SearchResultsTarget? Focus = null)
{
    public const string Route = "/search";
    public const int MaxQueryLength = 256;
    public const int MaxTargets = 100;

    public bool IsGlobal => Targets.All(t => t.Share is null);

    /// <param name="targets">Search targets as (share, sub path) in server terms; a null share means all shares.</param>
    /// <param name="focus">The share/folder to open and highlight on the page (from "n more in …").</param>
    public static SearchResultsRequest Create(string query, IEnumerable<(string? Share, string Sub)> targets,
        bool includeRecycleBin, Guid userId, (string Share, string Sub)? focus = null)
        => new(query,
            targets.Select(t => t.Share is null ? new SearchResultsTarget(null, "") : ToWeb(t.Share, t.Sub, userId)).ToList(),
            includeRecycleBin,
            focus is { } f ? ToWeb(f.Share, f.Sub, userId) : null);

    /// <summary>The targets in server terms for <c>ISearchService</c>.</summary>
    public List<(string? Share, string Sub)> ToSearchTargets(Guid userId)
    {
        if (IsGlobal)
            return [(null, "")];
        return Targets.Where(t => !string.IsNullOrWhiteSpace(t.Share))
            .Select(t => t.Share!.Equals(SearchHitNavigation.HomeRoute, StringComparison.OrdinalIgnoreCase)
                ? ((string?)HomeDirectoryService.ShareName,
                   $"{HomeDirectoryService.HomePathOf(userId)}/{Clean(t.Path)}".TrimEnd('/'))
                : (t.Share, Clean(t.Path)))
            .ToList();
    }

    /// <summary>Human-readable scope, e.g. "docs/reports, archive".</summary>
    public string Describe(string allSharesLabel) => IsGlobal
        ? allSharesLabel
        : string.Join(", ", Targets.Where(t => t.Share is not null)
            .Select(t => t.Path.Length == 0 ? t.Share : $"{t.Share}/{t.Path}"));

    public string Serialize() => JsonSerializer.Serialize(this);

    /// <summary>Reads the history state; null when it is missing or not a search request.</summary>
    public static SearchResultsRequest? Parse(string? state)
    {
        if (string.IsNullOrWhiteSpace(state))
            return null;
        try
        {
            var r = JsonSerializer.Deserialize<SearchResultsRequest>(state);
            if (r?.Query is null || r.Targets is null)
                return null;
            return r with
            {
                Query = r.Query.Length > MaxQueryLength ? r.Query[..MaxQueryLength] : r.Query,
                Targets = r.Targets.Where(t => t is not null).Take(MaxTargets)
                    .Select(t => t with { Path = Clean(t.Path) }).ToList(),
                Focus = r.Focus is { Share: not null } f ? f with { Path = Clean(f.Path) } : null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Maps a server target to web terms: the own home becomes "user" + home-relative path.</summary>
    public static SearchResultsTarget ToWeb(string share, string sub, Guid userId)
    {
        sub = Clean(sub);
        var home = HomeDirectoryService.HomePathOf(userId);
        if (!share.Equals(HomeDirectoryService.ShareName, StringComparison.OrdinalIgnoreCase))
            return new(share, sub);
        if (sub.Equals(home, StringComparison.OrdinalIgnoreCase))
            return new(SearchHitNavigation.HomeRoute, "");
        return sub.StartsWith(home + "/", StringComparison.OrdinalIgnoreCase)
            ? new(SearchHitNavigation.HomeRoute, sub[(home.Length + 1)..])
            : new(share, sub);
    }

    /// <summary>
    /// Where "n more" of a share leads: the searched folder when a target is a folder of that
    /// share, otherwise the share itself. Share names compare case-insensitively: the scope may
    /// come from the URL as typed, the hits carry the stored name (which the result keeps).
    /// </summary>
    /// <param name="targets">Search targets as (share, sub path) in server terms.</param>
    public static SearchResultsTarget FocusFor(string share, IEnumerable<(string? Share, string Sub)> targets, Guid userId)
    {
        var folder = targets
            .Where(t => t.Share is not null)
            .Select(t => ToWeb(t.Share!, t.Sub, userId))
            .FirstOrDefault(t => string.Equals(t.Share, share, StringComparison.OrdinalIgnoreCase) && t.Path.Length > 0);
        return new SearchResultsTarget(share, folder?.Path ?? "");
    }

    private static string Clean(string? path) => (path ?? "").Replace('\\', '/').Trim('/');
}

/// <summary>File-type categories of the results filter.</summary>
public enum SearchTypeFilter { All, Folders, Files, Documents, Images, Media, Archives }

/// <summary>
/// "Modified within" periods of the results filter, counted back from now (the server does not
/// know the viewer's time zone, so no calendar days); <c>Custom</c> uses From/To.
/// </summary>
public enum SearchDateFilter { Any, Day, Week, Month, Year, Custom }

/// <summary>How the refine field of the results page searches: names locally, or the full text.</summary>
public enum SearchRefineMode { Name, Content }

/// <summary>
/// Narrows the hits shown on the results page; the main search is not repeated.
/// <paramref name="Text"/> matches names and paths of the loaded hits. <paramref name="ContentHits"/>
/// (ids from a full-text search of the refine term over the same scope) keeps only hits that
/// search found. <paramref name="From"/>/<paramref name="To"/> (inclusive, UTC days) apply to
/// <see cref="SearchDateFilter.Custom"/>.
/// </summary>
public sealed record SearchResultsFilter(
    string Text, SearchTypeFilter Type, SearchDateFilter Modified,
    DateOnly? From = null, DateOnly? To = null, IReadOnlySet<string>? ContentHits = null)
{
    private static readonly HashSet<string> DocumentIcons =
        ["file_pdf.svg", "file_word.svg", "file_excel.svg", "file_powerpoint.svg", "file_text.svg"];

    public bool IsActive => !string.IsNullOrWhiteSpace(Text) || Type != SearchTypeFilter.All || HasDateBounds
        || ContentHits is not null;

    private bool HasDateBounds => Modified switch
    {
        SearchDateFilter.Any => false,
        SearchDateFilter.Custom => From is not null || To is not null,
        _ => true,
    };

    public bool Matches(FileDocument hit, DateTime nowUtc)
        => MatchesText(hit) && MatchesType(hit) && MatchesDate(hit, nowUtc)
           && (ContentHits is null || ContentHits.Contains(hit.Id));

    private bool MatchesText(FileDocument hit)
    {
        var text = Text.Trim();
        return text.Length == 0
            || hit.FileName.Contains(text, StringComparison.CurrentCultureIgnoreCase)
            || hit.SharePath.Replace('\\', '/').Contains(text, StringComparison.CurrentCultureIgnoreCase);
    }

    private bool MatchesType(FileDocument hit) => Type switch
    {
        SearchTypeFilter.Folders => hit.IsDirectory,
        SearchTypeFilter.Files => !hit.IsDirectory,
        SearchTypeFilter.Documents => !hit.IsDirectory && DocumentIcons.Contains(FileHelper.GetIcon(hit.FileName)),
        SearchTypeFilter.Images => !hit.IsDirectory && FileHelper.GetPreviewKind(hit.FileName) == PreviewKind.Image,
        SearchTypeFilter.Media => !hit.IsDirectory && FileHelper.GetPreviewKind(hit.FileName) is PreviewKind.Video or PreviewKind.Audio,
        SearchTypeFilter.Archives => !hit.IsDirectory && FileHelper.IsArchive(hit.FileName),
        _ => true,
    };

    private bool MatchesDate(FileDocument hit, DateTime nowUtc)
    {
        if (Modified == SearchDateFilter.Custom)
        {
            var day = DateOnly.FromDateTime(hit.Modified.ToUniversalTime());
            return (From is null || day >= From) && (To is null || day <= To);
        }

        var since = Modified switch
        {
            SearchDateFilter.Day => nowUtc.AddDays(-1),
            SearchDateFilter.Week => nowUtc.AddDays(-7),
            SearchDateFilter.Month => nowUtc.AddDays(-30),
            SearchDateFilter.Year => nowUtc.AddYears(-1),
            _ => (DateTime?)null,
        };
        return since is null || hit.Modified.ToUniversalTime() >= since;
    }
}
