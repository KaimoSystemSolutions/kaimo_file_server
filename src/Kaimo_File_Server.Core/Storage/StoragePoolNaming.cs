using System.Collections.Generic;
using System.IO;

namespace Kaimo_File_Server.Core.Storage;

/// <summary>
/// Resolves the label shown for a storage pool. Administrators may assign a
/// friendly name (e.g. "SSD_Pool01") to a pool path (e.g. "/mnt/pool01"); when
/// none is set the final path component is used. The custom names live in the
/// config store under <see cref="ConfigKey"/> as a path→name map, so renaming a
/// pool never touches the underlying mount path used for file I/O.
/// </summary>
public static class StoragePoolNaming
{
    /// <summary>Config key holding the path→custom-name map (a JSON object).</summary>
    public const string ConfigKey = "storage.poolNames";

    /// <summary>Normalizes a pool path into the stable key used in the map.</summary>
    public static string NormalizeKey(string poolPath)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(poolPath));

    /// <summary>The fallback name derived from the path's final component.</summary>
    public static string DerivedName(string poolPath)
    {
        var name = Path.GetFileName(NormalizeKey(poolPath));
        return string.IsNullOrEmpty(name) ? poolPath : name;
    }

    /// <summary>
    /// The custom name set for <paramref name="poolPath"/> when it is present and
    /// non-blank, otherwise the derived name.
    /// </summary>
    public static string Resolve(
        IReadOnlyDictionary<string, string>? customNames, string poolPath)
    {
        if (customNames is not null
            && customNames.TryGetValue(NormalizeKey(poolPath), out var custom)
            && !string.IsNullOrWhiteSpace(custom))
            return custom.Trim();

        return DerivedName(poolPath);
    }
}
