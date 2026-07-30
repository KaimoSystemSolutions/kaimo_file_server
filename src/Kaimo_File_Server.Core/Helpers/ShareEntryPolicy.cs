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
/// Rules apply to the first share-relative path segment only.
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

    public static ShareEntryClassification Classify(string? shareRelativePath)
    {
        var normalized = ShareRelativePath.Normalize(shareRelativePath);
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

    public static bool IsVisibleInFileBrowser(string? shareRelativePath) =>
        Classify(shareRelativePath).IsVisibleInFileBrowser;

    public static bool IsInternalPath(string? shareRelativePath) =>
        Classify(shareRelativePath).Kind == ShareEntryKind.Internal;

    public static bool IsRecycleBinPath(string? shareRelativePath) =>
        Classify(shareRelativePath).Kind == ShareEntryKind.RecycleBin;

    /// <summary>
    /// Returns whether a user-driven create, upload, replace, move, or rename
    /// would claim a namespace owned by Kaimo. Reading, restoring from, and
    /// deleting existing recycle-bin entries are separate operations.
    /// </summary>
    public static bool IsReservedForUserWrites(string? shareRelativePath) =>
        Classify(shareRelativePath).Kind != ShareEntryKind.Regular;
}
