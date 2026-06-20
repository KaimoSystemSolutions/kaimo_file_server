using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
    /// Reserved device names under Windows (case-insensitive).
    /// </summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// Maximum length of a single file/folder name under Windows.
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

        if (name.Length > MaxNameLength)
            errors.Add(string.Format(Resources.Web_Validation_NameTooLong, MaxNameLength, name.Length));

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

        var baseName = Path.GetFileNameWithoutExtension(name);
        if (ReservedNames.Contains(baseName))
            errors.Add(string.Format(Resources.Web_Validation_ReservedName, baseName));

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

        // Handle reserved names by appending an underscore
        var baseName = Path.GetFileNameWithoutExtension(sanitized);
        if (ReservedNames.Contains(baseName))
        {
            var ext = Path.GetExtension(sanitized);
            sanitized = baseName + "_" + ext;
        }

        // Truncate to the maximum length (keeping the extension)
        if (sanitized.Length > maxLength)
        {
            var ext = Path.GetExtension(sanitized);
            var maxBase = maxLength - ext.Length;
            sanitized = sanitized.Substring(0, Math.Max(1, maxBase)) + ext;
        }

        return string.IsNullOrWhiteSpace(sanitized) ? "_" : sanitized;
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