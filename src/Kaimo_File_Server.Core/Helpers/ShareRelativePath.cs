namespace Kaimo_File_Server.Core.Helpers;

/// <summary>
/// Single source of truth for path normalization across the entire system.
///
/// Design decisions:
///   - Paths are ALWAYS relative to the share root (never include the share name).
///   - Forward slashes only, no leading or trailing slashes.
///   - The share root itself is represented as "" (empty string).
///   - ShareId (Guid) is the stable foreign key — not the share name.
///     This means renaming a share never invalidates ACLs or metadata.
///
/// Every component that touches file paths must use this class:
///   - AclService (permission checks)
///   - AclEditor (UI for managing permissions)
///   - FileSystemStorage (disk I/O)
///   - SmbFileSystem (SMB protocol adapter)
///   - FileVersionService (versioning)
///   - FileBrowser (UI for browsing files)
///
/// Examples:
///   Normalize(@"\docs\sub\file.txt")  → "docs/sub/file.txt"
///   Normalize("docs/sub/")            → "docs/sub"
///   Normalize("")                     → ""
///   Normalize(null)                   → ""
///   Normalize("/")                    → ""
///   GetParent("docs/sub/file.txt")    → "docs/sub"
///   GetParent("file.txt")             → ""
///   GetParent("")                     → ""
///   GetFileName("docs/sub/file.txt")  → "file.txt"
///   BuildHierarchy("a/b/c.txt")       → ["", "a", "a/b", "a/b/c.txt"]
///   Combine("docs", "file.txt")       → "docs/file.txt"
/// </summary>
public static class ShareRelativePath
{
    /// <summary>
    /// Normalizes any path to the canonical format:
    /// forward slashes, no leading/trailing slashes, no double slashes.
    /// </summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        var normalized = path
            .Replace('\\', '/')
            .Trim('/');

        // Collapse double slashes (defensive, shouldn't happen in practice)
        while (normalized.Contains("//"))
            normalized = normalized.Replace("//", "/");

        return normalized;
    }

    /// <summary>
    /// Returns the parent directory path, or "" if already at root.
    /// </summary>
    public static string GetParent(string? path)
    {
        var normalized = Normalize(path);
        if (string.IsNullOrEmpty(normalized))
            return "";

        var lastSlash = normalized.LastIndexOf('/');
        return lastSlash > 0 ? normalized[..lastSlash] : "";
    }

    /// <summary>
    /// Returns just the file or directory name (last segment).
    /// Returns "(root)" for the share root.
    /// </summary>
    public static string GetFileName(string? path)
    {
        var normalized = Normalize(path);
        if (string.IsNullOrEmpty(normalized))
            return "(root)";

        var lastSlash = normalized.LastIndexOf('/');
        return lastSlash >= 0 ? normalized[(lastSlash + 1)..] : normalized;
    }

    /// <summary>
    /// Builds the full path hierarchy from root to the given path.
    /// Used by AclService for inheritance resolution.
    ///
    /// Example: "docs/sub/file.txt" → ["", "docs", "docs/sub", "docs/sub/file.txt"]
    /// </summary>
    public static List<string> BuildHierarchy(string? path)
    {
        var normalized = Normalize(path);
        var paths = new List<string> { "" }; // Share root is always included

        if (string.IsNullOrEmpty(normalized))
            return paths;

        var segments = normalized.Split('/');
        for (var i = 0; i < segments.Length; i++)
            paths.Add(string.Join("/", segments.Take(i + 1)));

        return paths;
    }

    /// <summary>
    /// Combines a parent path and a child name into a normalized path.
    /// </summary>
    public static string Combine(string? parent, string? child)
    {
        var normalizedParent = Normalize(parent);
        var normalizedChild = Normalize(child);

        if (string.IsNullOrEmpty(normalizedParent))
            return normalizedChild;
        if (string.IsNullOrEmpty(normalizedChild))
            return normalizedParent;

        return $"{normalizedParent}/{normalizedChild}";
    }

    /// <summary>
    /// Validates that a path contains no dangerous patterns.
    /// Returns false for path traversal attempts or invalid characters.
    ///
    /// The traversal check is SEGMENT-based, not a naive substring match:
    /// a ".." is only rejected when it is a whole path segment. This keeps
    /// legitimate names such as "report..final.txt" or "..gitignore" valid
    /// while still rejecting "../", "a/../b", "sub/.." etc.
    /// </summary>
    public static bool IsValid(string? path)
    {
        if (path is null)
            return true; // null normalizes to "", which is valid (root)

        // Reject NUL bytes anywhere (checked on the raw input — Normalize
        // does not strip them).
        if (path.IndexOf('\0') >= 0)
            return false;

        var normalized = Normalize(path);
        if (normalized.Length == 0)
            return true;

        foreach (var segment in normalized.Split('/'))
        {
            if (segment == "..")
                return false;
        }

        return true;
    }

    /// <summary>
    /// Validates untrusted share-relative input before normalization and returns
    /// its canonical representation.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="Normalize"/>, this method never turns an absolute path
    /// into an apparently safe relative path. It also rejects control
    /// characters, traversal segments, and (by default) Kaimo's reserved
    /// <c>.kaimo-*</c> top-level namespaces. Single-dot segments are removed.
    /// </remarks>
    public static bool TryNormalizeStrict(
        string? path,
        out string normalized,
        bool allowRoot = true,
        bool allowInternalNamespace = false)
    {
        normalized = "";
        if (path is null)
            return false;

        if (path.Length > 0 &&
            (path[0] is '/' or '\\' ||
             path.Length >= 2 && path[1] == ':'))
            return false;

        foreach (char character in path)
        {
            if (char.IsControl(character))
                return false;
        }

        string slashPath = path.Replace('\\', '/');
        var segments = new List<string>();
        foreach (string segment in slashPath.Split(
                     '/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
                continue;
            if (segment == "..")
                return false;
            segments.Add(segment);
        }

        normalized = string.Join('/', segments);
        if (normalized.Length == 0)
            return allowRoot &&
                   (path.Length == 0 || path == "." || path == "./");

        if (!allowInternalNamespace &&
            ShareEntryPolicy.IsInternalPath(normalized))
        {
            normalized = "";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Resolves an untrusted share-relative path beneath <paramref name="rootPath"/>.
    /// Raw input is validated before normalization, and the fully resolved result
    /// must be either the root itself or a descendant on a complete path boundary.
    /// </summary>
    public static string ToContainedAbsolutePath(
        string rootPath,
        string relativePath,
        bool allowRoot = true,
        bool allowInternalNamespace = true)
    {
        if (!TryNormalizeStrict(
                relativePath, out string normalized, allowRoot,
                allowInternalNamespace))
            throw new UnauthorizedAccessException(
                "Path is not a valid share-relative path.");

        string rootFull = Path.GetFullPath(rootPath);
        string pathRoot = Path.GetPathRoot(rootFull) ?? "";
        if (rootFull.Length > pathRoot.Length)
        {
            rootFull = rootFull.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        string rootWithSeparator = rootFull.EndsWith(
            Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;
        string full = normalized.Length == 0
            ? rootFull
            : Path.GetFullPath(Path.Combine(
                rootWithSeparator,
                normalized.Replace('/', Path.DirectorySeparatorChar)));

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(full, rootFull, comparison) &&
            !full.StartsWith(rootWithSeparator, comparison))
            throw new UnauthorizedAccessException(
                "Path escapes its configured root.");

        return full;
    }

    /// <summary>
    /// Calculates the depth of a path (number of segments).
    /// "" → 0, "docs" → 1, "docs/sub/file.txt" → 3
    /// </summary>
    public static int GetDepth(string? path)
    {
        var normalized = Normalize(path);
        if (string.IsNullOrEmpty(normalized))
            return 0;
        return normalized.Split('/').Length;
    }
}
