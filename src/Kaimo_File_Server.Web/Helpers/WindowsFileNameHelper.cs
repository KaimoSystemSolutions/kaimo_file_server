using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>
/// Plattformunabhängige Helper-Klasse zur Validierung und Bereinigung
/// von Datei- und Ordnernamen nach Windows-NTFS-Regeln.
/// </summary>
public static class WindowsFileNameHelper
{
    /// <summary>
    /// Zeichen, die in Windows-Datei- und Ordnernamen verboten sind.
    /// Direkt aus der .NET-Runtime-Quelle (Path.Windows.cs).
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
    /// Zeichen, die in Windows-Pfaden verboten sind (weniger restriktiv als Dateinamen).
    /// Direkt aus der .NET-Runtime-Quelle (Path.Windows.cs).
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
    /// Reservierte Gerätenamen unter Windows (case-insensitive).
    /// </summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// Maximale Länge eines einzelnen Datei-/Ordnernamens unter Windows.
    /// </summary>
    public const int MaxNameLength = 255;

    /// <summary>
    /// Prüft, ob ein Datei- oder Ordnername unter Windows gültig ist.
    /// </summary>
    public static bool IsValid(string name)
    {
        return GetValidationErrors(name).Count == 0;
    }

    /// <summary>
    /// Prüft, ob ein kompletter Pfad unter Windows gültig ist.
    /// Validiert den Pfad selbst mit InvalidPathChars und jedes Segment als Dateinamen.
    /// </summary>
    public static bool IsValidPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        // Pfad darf keine InvalidPathChars enthalten
        if (path.Any(c => InvalidPathChars.Contains(c)))
            return false;

        var parts = path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i];

            // Laufwerksbuchstabe überspringen
            if (i == 0 && part.Length == 2 && char.IsLetter(part[0]) && part[1] == ':')
                continue;

            if (!IsValid(part))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Gibt eine Liste aller Validierungsfehler für den Namen zurück.
    /// Leere Liste = gültig.
    /// </summary>
    public static List<string> GetValidationErrors(string name)
    {
        var errors = new List<string>();

        if (string.IsNullOrEmpty(name))
        {
            errors.Add("Name darf nicht leer sein.");
            return errors;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            errors.Add("Name darf nicht nur aus Leerzeichen bestehen.");
            return errors;
        }

        if (name.Length > MaxNameLength)
            errors.Add($"Name überschreitet die maximale Länge von {MaxNameLength} Zeichen ({name.Length}).");

        var found = name.Where(c => InvalidFileNameChars.Contains(c)).Distinct().ToList();
        if (found.Count > 0)
        {
            var display = string.Join(", ", found.Select(FormatChar));
            errors.Add($"Ungültige Zeichen: {display}");
        }

        if (name.EndsWith('.'))
            errors.Add("Name darf nicht mit einem Punkt enden.");

        if (name.EndsWith(' '))
            errors.Add("Name darf nicht mit einem Leerzeichen enden.");

        var baseName = Path.GetFileNameWithoutExtension(name);
        if (ReservedNames.Contains(baseName))
            errors.Add($"'{baseName}' ist ein reservierter Windows-Gerätename.");

        return errors;
    }

    /// <summary>
    /// Bereinigt einen Namen, sodass er unter Windows gültig ist.
    /// Ungültige Zeichen werden durch <paramref name="replacement"/> ersetzt.
    /// </summary>
    /// <param name="name">Der zu bereinigende Name.</param>
    /// <param name="replacement">Ersatzzeichen für ungültige Zeichen (Standard: '_').</param>
    /// <param name="maxLength">Maximale Länge des Ergebnisses (Standard: 255).</param>
    public static string Sanitize(string name, char replacement = '_', int maxLength = MaxNameLength)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "_";

        // Ungültige Zeichen ersetzen
        var sanitized = new string(name.Select(c =>
            InvalidFileNameChars.Contains(c) ? replacement : c
        ).ToArray());

        // Mehrfache Replacement-Zeichen zusammenfassen
        if (replacement != '\0')
        {
            var pattern = Regex.Escape(replacement.ToString()) + "{2,}";
            sanitized = Regex.Replace(sanitized, pattern, replacement.ToString());
        }

        // Trailing dots und spaces entfernen
        sanitized = sanitized.TrimEnd('.', ' ');

        // Reservierte Namen behandeln: Unterstrich anhängen
        var baseName = Path.GetFileNameWithoutExtension(sanitized);
        if (ReservedNames.Contains(baseName))
        {
            var ext = Path.GetExtension(sanitized);
            sanitized = baseName + "_" + ext;
        }

        // Auf maximale Länge kürzen (Extension beibehalten)
        if (sanitized.Length > maxLength)
        {
            var ext = Path.GetExtension(sanitized);
            var maxBase = maxLength - ext.Length;
            sanitized = sanitized.Substring(0, Math.Max(1, maxBase)) + ext;
        }

        return string.IsNullOrWhiteSpace(sanitized) ? "_" : sanitized;
    }

    /// <summary>
    /// Bereinigt einen kompletten Pfad (jedes Segment einzeln mit InvalidFileNameChars,
    /// zusätzlich InvalidPathChars auf Pfad-Ebene).
    /// Laufwerksbuchstaben (z.B. "C:") bleiben erhalten.
    /// </summary>
    public static string SanitizePath(string path, char replacement = '_')
    {
        if (string.IsNullOrWhiteSpace(path))
            return "_";

        // Zuerst InvalidPathChars auf Gesamtpfad-Ebene ersetzen
        var cleaned = new string(path.Select(c =>
            InvalidPathChars.Contains(c) ? replacement : c
        ).ToArray());

        // Pfad-Separatoren normalisieren
        var parts = cleaned.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>();

        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i];

            // Laufwerksbuchstabe beibehalten (z.B. "C:")
            if (i == 0 && part.Length == 2 && char.IsLetter(part[0]) && part[1] == ':')
            {
                result.Add(part);
                continue;
            }

            // Jedes Segment mit den strengeren InvalidFileNameChars bereinigen
            result.Add(Sanitize(part, replacement));
        }

        var separator = Path.DirectorySeparatorChar.ToString();
        var joined = string.Join(separator, result);

        // Ursprünglichen Root-Separator beibehalten
        if (path.StartsWith("/") || path.StartsWith("\\"))
            joined = separator + joined;

        return joined;
    }

    /// <summary>
    /// Formatiert ein Zeichen für die Fehlerausgabe.
    /// </summary>
    private static string FormatChar(char c)
    {
        if (c < 32) return $"0x{(int)c:X2}";
        return $"'{c}'";
    }
}