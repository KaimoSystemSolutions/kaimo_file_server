using System.Text.Json;

namespace Kaimo_File_Server.Core.Services.DataServices;

/// <summary>
/// Snapshot of the share-relative paths that existed after a successful two-way
/// sync. It is the memory the reconciliation engine needs to tell a genuine new
/// item ("was not here last time") apart from a deletion ("was here last time,
/// now gone on one side"). Only meaningful while
/// <see cref="Kaimo_File_Server.Core.Domain.CloudSyncAdvancedSettings.SyncDeletions"/>
/// is enabled; otherwise no manifest is produced or consumed.
/// </summary>
public sealed class SyncManifest
{
    /// <summary>
    /// Paths are compared case-insensitively to match the reconciliation engine,
    /// which keys both endpoints' listings with <see cref="StringComparer.OrdinalIgnoreCase"/>.
    /// </summary>
    public HashSet<string> Paths { get; }

    public SyncManifest()
        => Paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public SyncManifest(IEnumerable<string> paths)
        => Paths = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the previous run recorded this exact share-relative path.</summary>
    public bool Contains(string path) => Paths.Contains(path);

    public string Serialize()
        => JsonSerializer.Serialize(Paths);

    /// <summary>
    /// Rebuilds a manifest from its persisted JSON. A null, blank, or malformed
    /// value yields <c>null</c> so callers treat it as "no baseline yet" rather
    /// than an empty tree (which would look like everything was deleted).
    /// </summary>
    public static SyncManifest? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var paths = JsonSerializer.Deserialize<List<string>>(json);
            return paths is null ? null : new SyncManifest(paths);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
