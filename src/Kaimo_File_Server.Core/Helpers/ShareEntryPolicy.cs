namespace Kaimo_File_Server.Core.Helpers;

/// <summary>
/// Semantic type of a special entry in a share. Presentation layers can map
/// this stable classification to icons or actions without repeating path-name
/// comparisons.
/// </summary>
public enum ShareEntryKind
{
    Regular,
    RecycleBin,
    Internal
}

/// <summary>
/// Describes how a top-level share namespace is exposed to users.
/// </summary>
public readonly record struct ShareEntryClassification(
    ShareEntryKind Kind,
    bool IsVisibleInFileBrowser);

/// <summary>
/// Central registry for reserved and specially presented share namespaces.
/// Rules apply to the first share-relative path segment. A share with a
/// <c>rootDepth</c> &gt; 0 (the home-folder share, see
/// <c>ShareDefinition.RecycleRootDepth</c>) additionally applies them to the first
/// segment below that depth, so every home has its own <c>&lt;home&gt;/.RECYCLE_BIN</c>.
/// </summary>
public static class ShareEntryPolicy
{
    public const string RecycleBinName = ".RECYCLE_BIN";
    public const string InternalNamespacePrefix = ".kaimo-";

    private enum MatchKind
    {
        Exact,
        Prefix
    }

    private readonly record struct Rule(
        string Pattern,
        MatchKind Match,
        ShareEntryClassification Classification);

    private static readonly Rule[] Rules =
    [
        new(
            RecycleBinName,
            MatchKind.Exact,
            new(ShareEntryKind.RecycleBin, IsVisibleInFileBrowser: true)),
        new(
            InternalNamespacePrefix,
            MatchKind.Prefix,
            new(ShareEntryKind.Internal, IsVisibleInFileBrowser: false))
    ];

    private static readonly ShareEntryClassification Regular =
        new(ShareEntryKind.Regular, IsVisibleInFileBrowser: true);

    public static ShareEntryClassification Classify(string? shareRelativePath, int rootDepth = 0)
    {
        var normalized = ShareRelativePath.Normalize(shareRelativePath);
        var classification = ClassifyFirstSegment(normalized);
        // Share-root rules always hold; a deeper root only adds reservations.
        return classification.Kind == ShareEntryKind.Regular && rootDepth > 0
            ? ClassifyFirstSegment(SplitRoot(normalized, rootDepth).Below)
            : classification;
    }

    /// <summary>
    /// Path an item lands on when moved to the recycle bin:
    /// <c>&lt;root&gt;/.RECYCLE_BIN/&lt;rest&gt;</c>. Throws for the root itself (e.g. a
    /// home folder), which has no recycle bin above it.
    /// </summary>
    public static string GetRecyclePath(string? shareRelativePath, int rootDepth = 0)
    {
        var (root, below) = SplitRoot(ShareRelativePath.Normalize(shareRelativePath), rootDepth);
        if (below.Length == 0)
            throw new InvalidOperationException(
                $"'{shareRelativePath}' is a recycle root and cannot be recycled.");
        return ShareRelativePath.Combine(ShareRelativePath.Combine(root, RecycleBinName), below);
    }

    // Splits off the first rootDepth segments; a shorter path is all root.
    private static (string Root, string Below) SplitRoot(string normalized, int rootDepth)
    {
        if (rootDepth <= 0)
            return ("", normalized);
        string[] segments = normalized.Length == 0 ? [] : normalized.Split('/');
        return segments.Length <= rootDepth
            ? (normalized, "")
            : (string.Join('/', segments[..rootDepth]), string.Join('/', segments[rootDepth..]));
    }

    private static ShareEntryClassification ClassifyFirstSegment(string normalized)
    {
        if (normalized.Length == 0)
            return Regular;

        var separator = normalized.IndexOf('/');
        var firstSegment = separator >= 0
            ? normalized[..separator]
            : normalized;

        foreach (var rule in Rules)
        {
            var matches = rule.Match switch
            {
                MatchKind.Exact => firstSegment.Equals(
                    rule.Pattern, StringComparison.OrdinalIgnoreCase),
                MatchKind.Prefix => firstSegment.StartsWith(
                    rule.Pattern, StringComparison.OrdinalIgnoreCase),
                _ => false
            };

            if (matches)
                return rule.Classification;
        }

        return Regular;
    }

    public static bool IsVisibleInFileBrowser(string? shareRelativePath, int rootDepth = 0) =>
        Classify(shareRelativePath, rootDepth).IsVisibleInFileBrowser;

    public static bool IsInternalPath(string? shareRelativePath, int rootDepth = 0) =>
        Classify(shareRelativePath, rootDepth).Kind == ShareEntryKind.Internal;

    public static bool IsRecycleBinPath(string? shareRelativePath, int rootDepth = 0) =>
        Classify(shareRelativePath, rootDepth).Kind == ShareEntryKind.RecycleBin;

    /// <summary>
    /// Whether search leaves a path out entirely (index, live indexing and the filename walk):
    /// any segment starting with a dot (".versions", ".git", ".kaimo-…") — except the share's or
    /// home's own recycle bin, whose contents are searchable on request.
    /// </summary>
    public static bool IsExcludedFromSearch(string? shareRelativePath, int rootDepth = 0)
    {
        var segments = ShareRelativePath.Normalize(shareRelativePath).Split('/', StringSplitOptions.RemoveEmptyEntries);
        // Position of the recycle-bin segment, mirroring Classify: share root first, then
        // the first segment below rootDepth.
        int bin = !IsRecycleBinPath(shareRelativePath, rootDepth) ? -1
            : segments[0].Equals(RecycleBinName, StringComparison.OrdinalIgnoreCase) ? 0
            : rootDepth;

        for (int i = 0; i < segments.Length; i++)
            if (i != bin && segments[i].StartsWith('.'))
                return true;
        return false;
    }

    /// <summary>
    /// Returns whether a user-driven create, upload, replace, move, or rename
    /// would claim a namespace owned by Kaimo. Reading, restoring from, and
    /// deleting existing recycle-bin entries are separate operations.
    /// </summary>
    public static bool IsReservedForUserWrites(string? shareRelativePath, int rootDepth = 0) =>
        Classify(shareRelativePath, rootDepth).Kind != ShareEntryKind.Regular;
}
