namespace Kaimo_File_Server.Web.Helpers;

/// <summary>
/// Text form of an editable path bar: "Root/sub/folder". Maps typed or pasted text back to
/// a '/'-separated path below the root. Navigation still goes through the normal permission
/// checks; this only keeps the text from addressing anything above the root. A leading root
/// name always means the root (the bar shows it first), so a subfolder named like the root is
/// reached as "Root/Root".
/// </summary>
public static class PathBarText
{
    public static string Format(string rootName, string relativePath)
        => relativePath.Length == 0 ? rootName : $"{rootName}/{relativePath}";

    public static string ToRelative(string input, string rootName)
    {
        var segments = new List<string>();
        var normalized = input.Trim().Replace('\\', '/');
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

        // The bar shows the root name first; accept the text with or without it.
        if (segments.Count > 0 && string.Equals(segments[0], rootName, StringComparison.OrdinalIgnoreCase))
            segments.RemoveAt(0);
        return string.Join('/', segments);
    }
}
