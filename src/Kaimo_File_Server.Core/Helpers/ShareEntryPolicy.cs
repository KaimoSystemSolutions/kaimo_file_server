using System.Text.RegularExpressions;

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

        // Unlike the reserved first-segment namespaces above, this rule applies to the
        // FILE NAME at any depth: an interrupted write can leave a transient artifact
        // anywhere in the tree, not just at the share root.
        if (IsTransientWriteArtifact(normalized))
            return new ShareEntryClassification(ShareEntryKind.Internal, IsVisibleInFileBrowser: false);

        return Regular;
    }

    // Matches the artifact written by FileSystemStorage.WriteAsync:
    // ".{name}.kaimo-{32 hex}.tmp". Deliberately exact — a user file merely containing
    // ".kaimo-" (e.g. "my.kaimo-notes.txt") is NOT a transient artifact.
    private static readonly Regex TransientWriteArtifactPattern = new(
        @"^\..+\.kaimo-[0-9a-fA-F]{32}\.tmp$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// True if the last path segment is a transient write artifact
    /// (".{name}.kaimo-{32 hex}.tmp"). Applies at any depth, because an interrupted
    /// write can leave one anywhere. The shape is exact so a user-chosen filename cannot
    /// be misclassified.
    /// </summary>
    public static bool IsTransientWriteArtifact(string? shareRelativePath)
    {
        var normalized = ShareRelativePath.Normalize(shareRelativePath);
        if (normalized.Length == 0)
            return false;

        var lastSlash = normalized.LastIndexOf('/');
        var name = lastSlash >= 0 ? normalized[(lastSlash + 1)..] : normalized;
        return TransientWriteArtifactPattern.IsMatch(name);
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
