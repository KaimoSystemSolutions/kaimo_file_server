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

    public void Set(BrowserShareInfo source, IEnumerable<FileMetadata> items, bool deleteOnPaste, string? startPath)
    {
        Source = source;
        Items = items.ToList();
        DeleteOnPaste = deleteOnPaste;
        StartPath = startPath;
    }

    public void Clear()
    {
        Source = null;
        Items = [];
        DeleteOnPaste = false;
        StartPath = null;
        ToastId = null;
    }

    public void SetToastId(string toastId) => ToastId = toastId;

    public void ClearToastId() => ToastId = null;
}
