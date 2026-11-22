using Kaimo_File_Server.Search;

namespace Kaimo_File_Server.Web.Components.Search;

/// <summary>One share section of the search dropdown: its total hit count and the hits shown.</summary>
public sealed record SearchHitGroup(string Share, int Total, IReadOnlyList<FileDocument> Shown, bool Collapsed);

/// <summary>
/// Groups ranked hits by share for the dropdown. Shares keep the order of their best hit;
/// each shows its best <c>perShare</c> hits, sorted like the file browser (folders first, then
/// by name), and a collapsed share none (its count stays visible).
/// </summary>
public static class SearchHitGroups
{
    public static List<SearchHitGroup> Build(IEnumerable<FileDocument> hits, int perShare, ISet<string> collapsed)
        => hits
            .GroupBy(h => h.ShareName)
            .Select(g =>
            {
                var isCollapsed = collapsed.Contains(g.Key);
                var all = g.ToList();
                return new SearchHitGroup(g.Key, all.Count,
                    isCollapsed ? [] : SearchResultTree.SortLikeBrowser(all.Take(perShare), h => h.IsDirectory, h => h.FileName),
                    isCollapsed);
            })
            .ToList();

    /// <summary>A hit count for display: "n+" when the search stopped at its hit limit, so there may be more.</summary>
    public static string CountLabel(int count, bool capped)
        => capped ? $"{count}+" : count.ToString(System.Globalization.CultureInfo.CurrentCulture);
}
