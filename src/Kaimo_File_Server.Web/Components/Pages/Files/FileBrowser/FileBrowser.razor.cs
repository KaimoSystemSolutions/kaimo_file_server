
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

    private string CurrentShareRoute => string.IsNullOrWhiteSpace(ShareRoute)
        ? $"{OverviewRoute.TrimEnd('/')}/{Uri.EscapeDataString(ShareName)}"
        : ShareRoute.TrimEnd('/');

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
        int itemCount = SortedDirectories.Count() + SortedFiles.Count();
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

    // Single sequence for <Virtualize>: directories first (each group sorted), then files.
    // Materialized because Virtualize needs an indexable collection.
    private List<FileMetadata> SortedEntries => SortedDirectories.Concat(SortedFiles).ToList();

    private static bool IsRecycleBinEntry(FileMetadata entry) =>
        entry.IsDirectory &&
        ShareEntryPolicy.Classify(entry.Path).Kind ==
            ShareEntryKind.RecycleBin;

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
