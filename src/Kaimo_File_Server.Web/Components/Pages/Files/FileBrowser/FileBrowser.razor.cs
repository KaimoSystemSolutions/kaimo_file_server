
using System.Collections;
using System.Diagnostics;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Web.Components.Pages.Files.FileBrowser.components;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.DynamicHelpers;
using Kaimo_File_Server.Web.Helpers;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Kaimo_File_Server.Web.Components.Pages.Files.FileBrowser;

public partial class FileBrowser
{
    private static string T(string key) => Resources.ResourceManager.GetString(key) ?? key;

    [Parameter, EditorRequired] public IFileBrowserViewModel VM { get; set; } = default!;
    [Parameter] public string ShareName { get; set; } = "";
    [Parameter] public string? SubPath { get; set; }
    [Parameter] public string OverviewRoute { get; set; } = "/files";
    [Parameter] public string? ShareRoute { get; set; }

    /// <summary>
    /// When true, folder navigation and breadcrumbs stay on the current URL and reload the
    /// listing in place instead of routing to a sub-path URL. Used by the public share link
    /// browser so no folder name is ever appended after the link token in the address bar.
    /// </summary>
    [Parameter] public bool InPlaceNavigation { get; set; }

    // The folder currently shown when navigating in place (null before the first hop).
    private string? _inPlaceSubPath;

    // The sub-path to load: the in-place location when set, otherwise the routed one.
    private string EffectiveLoadSubPath =>
        InPlaceNavigation ? (_inPlaceSubPath ?? SubPath ?? "") : (SubPath ?? "");

    /// <summary>
    /// Human-friendly name of the share for the breadcrumb, used before the share has
    /// finished loading (and its real name is known). Callers that address the share by
    /// an opaque key — e.g. Cloud Access virtual shares, identified by a GUID — leave this
    /// unset, so the breadcrumb shows a neutral placeholder while loading instead of the key.
    /// </summary>
    [Parameter] public string? ShareDisplayName { get; set; }

    private string CurrentShareRoute => string.IsNullOrWhiteSpace(ShareRoute)
        ? $"{OverviewRoute.TrimEnd('/')}/{Uri.EscapeDataString(ShareName)}"
        : ShareRoute.TrimEnd('/');

    /// <summary>
    /// The share name shown in the breadcrumb. Prefers the loaded share's real name; while
    /// that is still unknown it falls back to a caller-supplied display name, then — only
    /// when even that is missing — to a loading placeholder or the raw share key. This keeps
    /// a previously opened share from lingering in the breadcrumb during a load.
    /// </summary>
    /// <summary>
    /// DOM key for a listing row (<c>data-path</c>, used by marquee selection and drag/drop).
    /// Route-relative rather than the absolute server path, so no internal folder name ever
    /// reaches the page markup — the public-link browser strips its confined root here too.
    /// </summary>
    private string RowKey(FileMetadata entry) => VM.RouteSubPathOf(VM.ShareRelativeOf(entry));

    private string CurrentShareDisplayName =>
        VM.CurrentBrowserShare?.Name
        ?? ShareDisplayName
        ?? (VM.IsLoading ? "…" : ShareName);

    // ========== Loading skeleton ==========

    /// <summary>Number of placeholder rows drawn while a directory is loading.</summary>
    private const int SkeletonRowCount = 9;

    /// <summary>
    /// Varied widths for the skeleton name bars so the placeholder rows look like a
    /// real, irregular file listing instead of a uniform block. Cycled per row.
    /// </summary>
    private static readonly string[] SkeletonNameWidths =
        ["62%", "43%", "78%", "35%", "55%", "70%", "48%", "66%", "40%"];

    // ========== Column Resize (5 Spalten, ohne Actions) ==========

    private Dictionary<string, double> _colWidths = new()
    {
        ["size"] = 90,
        ["created"] = 140,
        ["modified"] = 140,
        ["lastaccess"] = 140
    };

    // Name width: 0 = not yet resized → 1fr; > 0 = fixed px
    private double _nameWidth = 0;

    private const double ColMinWidth = 50;
    private DotNetObjectReference<FileBrowser>? _dotNetRef;

    /// <summary>Baut den grid-template-columns String (5 Spalten).</summary>
    private string GridColumns =>
        $"{(_nameWidth > 0 ? $"{_nameWidth}px" : "1fr")} " +
        $"{_colWidths["size"]}px " +
        $"{_colWidths["created"]}px " +
        $"{_colWidths["modified"]}px " +
        $"{_colWidths["lastaccess"]}px";

    private async Task OnResizeStart(MouseEventArgs e, int colIndex)
    {
        _dotNetRef ??= DotNetObjectReference.Create(this);
        await JS.InvokeVoidAsync("columnResize.start",
            _dotNetRef, colIndex, e.ClientX, ColMinWidth);
    }

    [JSInvokable]
    public void OnResizeEnd(string jsonWidths)
    {
        try
        {
            var widths = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double>>(jsonWidths);
            if (widths is null) return;

            foreach (var (col, w) in widths)
            {
                if (col == "name")
                {
                    _nameWidth = w >= ColMinWidth ? Math.Round(w) : 0;
                    continue;
                }

                if (_colWidths.ContainsKey(col) && w >= ColMinWidth)
                    _colWidths[col] = Math.Round(w);
            }
        }
        catch
        {
        }

        InvokeAsync(StateHasChanged);
    }

    // ========== Column Sorting ==========

    private static readonly float EmptyAreaHeightPx = 123f;
    private static readonly float ItemHeightPx = 41f;

    private float GetTableExtraPaddingPx()
    {
        int itemCount = SortedEntries.Count;
        float remainingHeight = EmptyAreaHeightPx - itemCount * ItemHeightPx;

        if (itemCount == 0)
            return 0;

        if (remainingHeight < 0)
            return 0;

        return remainingHeight;
    }

    private string? _sortColumn;
    private int _sortDirection; // 0 = default, 1 = descending, 2 = ascending

    private void OnHeaderClick(string column)
    {
        if (_sortColumn == column)
        {
            _sortDirection = (_sortDirection + 1) % 3;
            if (_sortDirection == 0)
                _sortColumn = null;
        }
        else
        {
            _sortColumn = column;
            _sortDirection = 1; // Erst absteigend
        }
    }

    private IEnumerable<FileMetadata> SortedDirectories => ApplySort(VM.Directories);
    private IEnumerable<FileMetadata> SortedFiles => ApplySort(VM.Files);

    // Cache for the materialized entry list. Sorting the whole directory and allocating a
    // new list is O(n log n); without this it ran on every render — and Blazor Server
    // re-renders the whole component on every click, selection change and context-menu open,
    // which made large folders lag badly. The cache is rebuilt only when the inputs that
    // affect ordering actually change: a fresh listing (VM.Items is reassigned per load, so
    // reference identity detects navigation/refresh/delete/paste) or a new sort setting.
    private List<FileMetadata>? _sortedEntriesCache;
    private object? _sortedEntriesItemsRef;
    private string? _sortedEntriesColumn;
    private int _sortedEntriesDirection = -1;

    // Single sequence for <Virtualize>: directories first (each group sorted), then files.
    // Materialized because Virtualize needs an indexable collection.
    private List<FileMetadata> SortedEntries
    {
        get
        {
            // Size sort depends on directory sizes that load asynchronously, so it must
            // re-sort live as those values arrive — never serve it from the cache.
            if (_sortColumn == "size" && _sortDirection != 0)
                return SortedDirectories.Concat(SortedFiles).ToList();

            if (_sortedEntriesCache is not null &&
                ReferenceEquals(_sortedEntriesItemsRef, VM.Items) &&
                _sortedEntriesColumn == _sortColumn &&
                _sortedEntriesDirection == _sortDirection)
            {
                return _sortedEntriesCache;
            }

            _sortedEntriesCache = SortedDirectories.Concat(SortedFiles).ToList();
            _sortedEntriesItemsRef = VM.Items;
            _sortedEntriesColumn = _sortColumn;
            _sortedEntriesDirection = _sortDirection;
            return _sortedEntriesCache;
        }
    }

    // Only the recycle-bin root itself (a top-level ".RECYCLE_BIN") gets the trash icon.
    // Folders nested inside it are ordinary directories and keep the folder icon.
    private static bool IsRecycleBinEntry(FileMetadata entry) =>
        entry.IsDirectory &&
        ShareRelativePath.GetDepth(entry.Path) == 1 &&
        ShareEntryPolicy.Classify(entry.Path).Kind ==
            ShareEntryKind.RecycleBin;

    /// <summary>
    /// Whether the entry is the recycle-bin root folder — gates the
    /// "empty recycle bin" command in the context menu and selection toolbar.
    /// </summary>
    public bool IsRecycleBinRoot(FileMetadata? entry) =>
        entry is not null && IsRecycleBinEntry(entry);

    private IEnumerable<FileMetadata> ApplySort(IEnumerable<FileMetadata> items)
    {
        if (_sortColumn is null || _sortDirection == 0)
            return items; // Default: alphabetisch nach Name (vom VM)

        Func<FileMetadata, object> keySelector = _sortColumn switch
        {
            "name" => f => f.Name.ToLowerInvariant(),
            "size" => f => f.IsDirectory ? (VM.GetDirectorySize(f) ?? 0L) : f.Size,
            "created" => f => f.CreatedAt,
            "modified" => f => f.ModifiedAt,
            "lastaccess" => f => f.LastAccessedAt ?? DateTime.MinValue,
            _ => f => f.Name.ToLowerInvariant()
        };

        return _sortDirection == 1
            ? items.OrderByDescending(keySelector)

            : items.OrderBy(keySelector);
    }
}
