namespace Kaimo_File_Server.Web.Helpers;

/// <summary>
/// Text form of an editable path bar: "Root/sub/folder". Maps typed or pasted text back to
/// a '/'-separated path below the root. Navigation still goes through the normal permission
/// checks; this only keeps the text from addressing anything above the root. A leading root
/// name always means the root (the bar shows it first), so a subfolder named like the root is
/// reached as "Root/Root".
/// <para>Absolute form (file browser): "/" stands for the shares overview, so the bar shows
/// "/Prefix/Root/sub" (prefix = the folders above a mounted root, usually empty) and text that
/// starts with "/" (or is a UNC path "\\server\Share\...") addresses a share from the overview.
/// Surrounding double quotes (Explorer's "Copy as path") are ignored. A path that ends in a
/// file opens the file's folder with the file selected (see FileBrowserViewModel.FileToSelect).</para>
/// </summary>
public static class PathBarText
{
    public static string Format(string rootName, string relativePath)
        => relativePath.Length == 0 ? rootName : $"{rootName}/{relativePath}";

    public static string ToRelative(string input, string rootName)
    {
        var segments = Segments(input);

        // The bar shows the root name first; accept the text with or without it.
        if (segments.Count > 0 && string.Equals(segments[0], rootName, StringComparison.OrdinalIgnoreCase))
            segments.RemoveAt(0);
        return string.Join('/', segments);
    }

    /// <summary>Absolute text of the bar: "/" + prefix + root + path.</summary>
    public static string FormatAbsolute(string rootPrefix, string rootName, string relativePath)
    {
        var prefix = rootPrefix.Trim('/');
        return "/" + (prefix.Length == 0 ? "" : prefix + "/") + Format(rootName, relativePath);
    }

    /// <summary>
    /// Path below the shares overview ("" = the overview itself) for text starting with "/" or
    /// a UNC path; null for relative text.
    /// </summary>
    public static string? ToAbsolute(string input)
        => Normalize(input).StartsWith('/') ? string.Join('/', Segments(input)) : null;

    /// <summary>
    /// The part of an absolute path below "prefix/root" (case-insensitive); null when the path
    /// lies elsewhere.
    /// </summary>
    public static string? BelowRoot(string absolute, string rootPrefix, string rootName)
    {
        var path = Segments(absolute);
        var root = Segments(rootPrefix);
        root.Add(rootName);
        if (path.Count < root.Count)
            return null;
        for (var i = 0; i < root.Count; i++)
            if (!string.Equals(path[i], root[i], StringComparison.OrdinalIgnoreCase))
                return null;
        return string.Join('/', path.Skip(root.Count));
    }

    // Explorer's "Copy as path" wraps the text in double quotes; '\' counts as '/'.
    private static string Normalize(string input)
        => input.Trim().Trim('"').Trim().Replace('\\', '/');

    // Clean segments: '\' as '/', no empty, "." or ".." segments (".." never climbs above the start).
    private static List<string> Segments(string input)
    {
        var segments = new List<string>();
        var normalized = Normalize(input);
        // A UNC path copied from Explorer/SMB ("\\server\Share\...") starts with the server name.
        var skipHost = normalized.StartsWith("//", StringComparison.Ordinal);
        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (skipHost) { skipHost = false; continue; }
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }
        return segments;
    }
}
