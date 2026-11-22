using Kaimo_File_Server.Search;

namespace Kaimo_File_Server.Web.Components.Search;

/// <summary>
/// A node of the search-results tree: a share (root), a folder on the way to a hit, or a hit.
/// A folder that is itself a hit and also contains hits is one node carrying <see cref="Hit"/>.
/// </summary>
public sealed class SearchTreeNode
{
    /// <summary>Unique across shares: "share" or "share/folder/file" (used for collapse state).</summary>
    public required string Key { get; init; }
    public required string Share { get; init; }
    public required string Name { get; init; }

    /// <summary>Share-relative path with forward slashes; empty for the share root.</summary>
    public string SharePath { get; init; } = "";

    public bool IsShare => SharePath.Length == 0;
    public bool IsDirectory { get; set; }
    public FileDocument? Hit { get; set; }

    /// <summary>Number of hits in this subtree (including the node itself).</summary>
    public int HitCount { get; set; }

    public List<SearchTreeNode> Children { get; } = [];
}

public sealed record SearchTreeRow(SearchTreeNode Node, int Depth);

/// <summary>Builds the file-tree view of the search results page from flat hits.</summary>
public static class SearchResultTree
{
    public static List<SearchTreeNode> Build(IEnumerable<FileDocument> hits)
    {
        var roots = new Dictionary<string, SearchTreeNode>(StringComparer.Ordinal);
        var nodes = new Dictionary<string, SearchTreeNode>(StringComparer.Ordinal);

        foreach (var hit in hits)
        {
            if (!roots.TryGetValue(hit.ShareName, out var node))
                roots[hit.ShareName] = node = new SearchTreeNode
                {
                    Key = hit.ShareName, Share = hit.ShareName, Name = hit.ShareName, IsDirectory = true
                };
            node.HitCount++;

            var parts = hit.SharePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                var path = string.Join('/', parts[..(i + 1)]);
                var key = hit.ShareName + "/" + path;
                if (!nodes.TryGetValue(key, out var child))
                {
                    nodes[key] = child = new SearchTreeNode
                    {
                        Key = key, Share = hit.ShareName, Name = parts[i], SharePath = path, IsDirectory = true
                    };
                    node.Children.Add(child);
                }
                node = child;
                node.HitCount++;
            }

            node.Hit = hit;
            node.IsDirectory = hit.IsDirectory || node.IsShare;
        }

        var result = roots.Values.OrderBy(r => r.Name, NameOrder).ToList();
        foreach (var root in result)
            Sort(root);
        return result;
    }

    /// <summary>Visible rows in display order; children of collapsed nodes are skipped.</summary>
    public static IEnumerable<SearchTreeRow> Flatten(IEnumerable<SearchTreeNode> roots, ISet<string> collapsed, int depth = 0)
    {
        foreach (var node in roots)
        {
            yield return new SearchTreeRow(node, depth);
            if (collapsed.Contains(node.Key))
                continue;
            foreach (var row in Flatten(node.Children, collapsed, depth + 1))
                yield return row;
        }
    }

    /// <summary>The node with <paramref name="key"/> anywhere in the tree (collapsed or not); null if absent.</summary>
    public static SearchTreeNode? Find(IEnumerable<SearchTreeNode> roots, string key,
        StringComparison comparison = StringComparison.Ordinal)
        => Flatten(roots, new HashSet<string>()).Select(r => r.Node).FirstOrDefault(n => n.Key.Equals(key, comparison));

    /// <summary>Key of the node for a share and share-relative folder (see <see cref="SearchTreeNode.Key"/>).</summary>
    public static string KeyOf(string share, string path)
    {
        path = path.Replace('\\', '/').Trim('/');
        return path.Length == 0 ? share : $"{share}/{path}";
    }

    // Alphabetical like the file browser's name order (culture-aware, so "Ä" sorts with "A").
    private static readonly StringComparer NameOrder = StringComparer.CurrentCultureIgnoreCase;

    /// <summary>Folders first, then files, each by name.</summary>
    public static List<T> SortLikeBrowser<T>(IEnumerable<T> items, Func<T, bool> isDirectory, Func<T, string> name)
        => items.OrderBy(i => isDirectory(i) ? 0 : 1).ThenBy(name, NameOrder).ToList();

    private static void Sort(SearchTreeNode node)
    {
        var sorted = SortLikeBrowser(node.Children, c => c.IsDirectory, c => c.Name);
        node.Children.Clear();
        node.Children.AddRange(sorted);
        foreach (var child in node.Children)
            Sort(child);
    }
}
