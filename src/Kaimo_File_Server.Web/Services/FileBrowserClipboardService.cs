using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Web.Components.ViewModels;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Per-circuit browser clipboard. Keeping this state outside a page component allows
/// paste operations after navigating from a local browser route to a virtual one.
/// </summary>
public sealed class FileBrowserClipboardService
{
    public BrowserShareInfo? Source { get; private set; }
    public IReadOnlyCollection<FileMetadata> Items { get; private set; } = [];
    public bool DeleteOnPaste { get; private set; }
    /// <summary>Directory the items were cut/copied from, needed to map them into the paste target.</summary>
    public string? StartPath { get; private set; }
    /// <summary>State-toast identity retained across browser route changes.</summary>
    public string? ToastId { get; private set; }
    /// <summary>
    /// Changes whenever the clipboard is set or cleared, so a finished paste clears only the
    /// clipboard it pasted, never one set meanwhile in this or another browser page.
    /// </summary>
    public int Generation { get; private set; }

    public void Set(BrowserShareInfo source, IEnumerable<FileMetadata> items, bool deleteOnPaste, string? startPath)
    {
        Generation++;
        Source = source;
        Items = items.ToList();
        DeleteOnPaste = deleteOnPaste;
        StartPath = startPath;
    }

    public void Clear()
    {
        Generation++;
        Source = null;
        Items = [];
        DeleteOnPaste = false;
        StartPath = null;
        ToastId = null;
    }

    public void SetToastId(string toastId) => ToastId = toastId;

    public void ClearToastId() => ToastId = null;

    // Generations of the cuts being pasted right now.
    private readonly HashSet<int> _cutPastesRunning = [];

    /// <summary>
    /// Claims the cut of <paramref name="generation"/> for one paste. False while it is already
    /// being pasted: batches no longer block the circuit, and a second paste of the same cut
    /// would copy the items again (into the same files, or a second place) and then also try to
    /// delete the originals. Release it with <see cref="EndCutPaste"/>.
    /// </summary>
    public bool TryBeginCutPaste(int generation) => _cutPastesRunning.Add(generation);

    public void EndCutPaste(int generation) => _cutPastesRunning.Remove(generation);
}
