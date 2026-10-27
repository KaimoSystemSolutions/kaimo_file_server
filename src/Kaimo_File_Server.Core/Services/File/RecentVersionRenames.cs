using System.Collections.Concurrent;

namespace Kaimo_File_Server.Core.Services.File;

/// <summary>
/// Remembers for a few seconds where a file's history went in a rename. Word (and Excel)
/// save by moving the document aside (<c>report.docx → ~WRL0001.tmp</c>), moving the new
/// content into place (<c>~WRD0000.tmp → report.docx</c>) and deleting the moved-aside
/// original; without this the document's history would follow the original into the bin.
/// When something is renamed onto a name whose history just left, that history comes back.
///
/// ponytail: per process; one save sequence arrives through one transport and therefore one
/// process. Register as a singleton: <see cref="FileVersionService"/> is scoped.
/// </summary>
public sealed class RecentVersionRenames
{
    /// <summary>How long after a rename its history can still be pulled back.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    private const int PruneThreshold = 1024;

    private readonly ConcurrentDictionary<(Guid ShareId, string Path), (string MovedTo, DateTime AtUtc)> _movedAway = new();

    /// <summary>The history of <paramref name="from"/> now lives at <paramref name="to"/>.</summary>
    public void Record(Guid shareId, string from, string to, DateTime nowUtc)
    {
        if (_movedAway.Count >= PruneThreshold)
            foreach (var entry in _movedAway)
                if (nowUtc - entry.Value.AtUtc > Window)
                    _movedAway.TryRemove(entry.Key, out _);

        _movedAway[(shareId, from)] = (to, nowUtc);
    }

    /// <summary>Where the history of <paramref name="path"/> went within the <see cref="Window"/>, if anywhere.</summary>
    public string? MovedAwayTo(Guid shareId, string path, DateTime nowUtc)
        => _movedAway.TryGetValue((shareId, path), out var entry) && nowUtc - entry.AtUtc <= Window
            ? entry.MovedTo
            : null;

    public void Forget(Guid shareId, string path) => _movedAway.TryRemove((shareId, path), out _);
}
