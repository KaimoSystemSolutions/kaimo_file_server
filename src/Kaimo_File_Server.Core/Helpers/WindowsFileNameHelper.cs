using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Language;

/// <summary>
/// Platform-independent helper for validating and sanitizing file and folder
/// names according to Windows NTFS rules.
/// </summary>
public static class WindowsFileNameHelper
{
    /// <summary>
    /// Characters that are forbidden in Windows file and folder names.
    /// Taken directly from the .NET runtime source (Path.Windows.cs).
    /// </summary>
    private static readonly char[] InvalidFileNameChars =
    [
        '\"', '<', '>', '|', '\0',
        (char)1, (char)2, (char)3, (char)4, (char)5, (char)6, (char)7, (char)8, (char)9, (char)10,
        (char)11, (char)12, (char)13, (char)14, (char)15, (char)16, (char)17, (char)18, (char)19, (char)20,
        (char)21, (char)22, (char)23, (char)24, (char)25, (char)26, (char)27, (char)28, (char)29, (char)30,
        (char)31, ':', '*', '?', '\\', '/'
    ];

    /// <summary>
    /// Characters that are forbidden in Windows paths (less restrictive than file names).
    /// Taken directly from the .NET runtime source (Path.Windows.cs).
    /// </summary>
    private static readonly char[] InvalidPathChars =
    [
        '|', '\0',
        (char)1, (char)2, (char)3, (char)4, (char)5, (char)6, (char)7, (char)8, (char)9, (char)10,
        (char)11, (char)12, (char)13, (char)14, (char)15, (char)16, (char)17, (char)18, (char)19, (char)20,
        (char)21, (char)22, (char)23, (char)24, (char)25, (char)26, (char)27, (char)28, (char)29, (char)30,
        (char)31
    ];

    /// <summary>
    /// Maximum length of a single file/folder name: 255 UTF-8 bytes, the stricter of the
    /// Windows (255 UTF-16 units) and ext4 (255 bytes) limits.
    /// </summary>
    public const int MaxNameLength = 255;

    /// <summary>
    /// Checks whether a file or folder name is valid under Windows.
    /// </summary>
    public static bool IsValid(string name)
    {
        return GetValidationErrors(name).Count == 0;
    }

    /// <summary>
    /// Checks whether a complete path is valid under Windows.
    /// Validates the path itself against InvalidPathChars and each segment as a file name.
    /// </summary>
    public static bool IsValidPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        // The path must not contain any InvalidPathChars
        if (path.Any(c => InvalidPathChars.Contains(c)))
            return false;

        var parts = path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i];

            // Skip the drive letter
            if (i == 0 && part.Length == 2 && char.IsLetter(part[0]) && part[1] == ':')
                continue;

            if (!IsValid(part))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Returns a list of all validation errors for the name.
    /// An empty list means the name is valid.
    /// </summary>
    public static List<string> GetValidationErrors(string name)
    {
        var errors = new List<string>();

        if (string.IsNullOrEmpty(name))
        {
            errors.Add(Resources.Web_Validation_NameEmpty);
            return errors;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            errors.Add(Resources.Web_Validation_NameWhitespace);
            return errors;
        }

        var byteCount = Encoding.UTF8.GetByteCount(name);
        if (byteCount > MaxNameLength)
            errors.Add(string.Format(Resources.Web_Validation_NameTooLong, MaxNameLength, byteCount));

        var found = name.Where(c => InvalidFileNameChars.Contains(c)).Distinct().ToList();
        if (found.Count > 0)
        {
            var display = string.Join(", ", found.Select(FormatChar));
            errors.Add(string.Format(Resources.Web_Validation_InvalidChars, display));
        }

        if (name.EndsWith('.'))
            errors.Add(Resources.Web_Validation_NameEndsWithDot);

        if (name.EndsWith(' '))
            errors.Add(Resources.Web_Validation_NameEndsWithSpace);

        // Windows reserves device names regardless of extension ("con.txt", "CON.tar.gz").
        if (SambaName.IsReservedDeviceName(name))
            errors.Add(string.Format(Resources.Web_Validation_ReservedName, name.Split('.', 2)[0]));

        return errors;
    }

    /// <summary>
    /// Sanitizes a name so that it is valid under Windows.
    /// Invalid characters are replaced with <paramref name="replacement"/>.
    /// </summary>
    /// <param name="name">The name to sanitize.</param>
    /// <param name="replacement">Replacement character for invalid characters (default: '_').</param>
    /// <param name="maxLength">Maximum length of the result (default: 255).</param>
    public static string Sanitize(string name, char replacement = '_', int maxLength = MaxNameLength)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "_";

        // Replace invalid characters
        var sanitized = new string(name.Select(c =>
            InvalidFileNameChars.Contains(c) ? replacement : c
        ).ToArray());

        // Collapse runs of replacement characters into a single one
        if (replacement != '\0')
        {
            var pattern = Regex.Escape(replacement.ToString()) + "{2,}";
            sanitized = Regex.Replace(sanitized, pattern, replacement.ToString());
        }

        // Remove trailing dots and spaces
        sanitized = sanitized.TrimEnd('.', ' ');

        // Reserved device names: suffix the part before the first dot ("CON.txt" -> "CON_.txt")
        if (SambaName.IsReservedDeviceName(sanitized))
        {
            var dot = sanitized.IndexOf('.');
            sanitized = dot < 0 ? sanitized + "_" : sanitized[..dot] + "_" + sanitized[dot..];
        }

        // Truncate to the maximum byte length, keeping the extension
        if (Encoding.UTF8.GetByteCount(sanitized) > maxLength)
        {
            var ext = Path.GetExtension(sanitized);
            if (Encoding.UTF8.GetByteCount(ext) > maxLength / 2)
                ext = "";
            var stem = sanitized[..^ext.Length];
            sanitized = TruncateUtf8(stem, maxLength - Encoding.UTF8.GetByteCount(ext)).TrimEnd('.', ' ') + ext;
        }

        return string.IsNullOrWhiteSpace(sanitized) ? "_" : sanitized;
    }

    /// <summary>
    /// Sanitizes every segment of a share-relative path ("bad:dir/file?.txt" ->
    /// "bad_dir/file_.txt"). "" stays "".
    /// </summary>
    public static string SanitizeRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;
        return string.Join('/', path.Split('/').Select(segment => Sanitize(segment)));
    }

    /// <summary>
    /// Like <see cref="SanitizeRelativePath"/>, but a segment that already exists keeps its name:
    /// entries created before this rule (or on disk directly) are still addressable, and only
    /// what a write would newly create is renamed. <paramref name="exists"/> receives the
    /// share-relative path of an invalid segment as it would be kept.
    /// </summary>
    public static string SanitizeNewSegments(string path, Func<string, bool> exists)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        var result = "";
        foreach (var segment in path.Split('/'))
        {
            var prefix = result.Length == 0 ? "" : result + "/";
            result = IsValid(segment) || exists(prefix + segment)
                ? prefix + segment
                : prefix + Sanitize(segment);
        }
        return result;
    }

    /// <summary>
    /// Windows-Explorer-style alternatives for a name that is taken:
    /// "a.txt" -> "a (2).txt", "a (3).txt", ...
    /// </summary>
    public static IEnumerable<string> NumberedVariants(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var n = 2; ; n++)
            yield return $"{stem} ({n}){ext}";
    }

    // Longest prefix of value that fits into maxBytes UTF-8 bytes, never splitting a surrogate pair.
    private static string TruncateUtf8(string value, int maxBytes)
    {
        var bytes = 0;
        var i = 0;
        while (i < value.Length)
        {
            var width = char.IsHighSurrogate(value[i]) && i + 1 < value.Length ? 2 : 1;
            var size = Encoding.UTF8.GetByteCount(value.AsSpan(i, width));
            if (bytes + size > maxBytes)
                break;
            bytes += size;
            i += width;
        }
        return value[..i];
    }

    /// <summary>
    /// Sanitizes a complete path (each segment individually with InvalidFileNameChars,
    /// plus InvalidPathChars at the path level).
    /// Drive letters (e.g. "C:") are preserved.
    /// </summary>
    public static string SanitizePath(string path, char replacement = '_')
    {
        if (string.IsNullOrWhiteSpace(path))
            return "_";

        // First replace InvalidPathChars across the whole path
        var cleaned = new string(path.Select(c =>
            InvalidPathChars.Contains(c) ? replacement : c
        ).ToArray());

        // Normalize path separators
        var parts = cleaned.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>();

        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i];

            // Preserve the drive letter (e.g. "C:")
            if (i == 0 && part.Length == 2 && char.IsLetter(part[0]) && part[1] == ':')
            {
                result.Add(part);
                continue;
            }

            // Sanitize each segment with the stricter InvalidFileNameChars
            result.Add(Sanitize(part, replacement));
        }

        var separator = Path.DirectorySeparatorChar.ToString();
        var joined = string.Join(separator, result);

        // Preserve the original root separator
        if (path.StartsWith("/") || path.StartsWith("\\"))
            joined = separator + joined;

        return joined;
    }

    /// <summary>
    /// Formats a character for display in error output.
    /// </summary>
    private static string FormatChar(char c)
    {
        if (c < 32) return $"0x{(int)c:X2}";
        return $"'{c}'";
    }
}