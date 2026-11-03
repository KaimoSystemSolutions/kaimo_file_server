using Kaimo_File_Server.Search;
using Kaimo_File_Server.Web.Components.ViewModels;
using Microsoft.AspNetCore.Components;

namespace Kaimo_File_Server.Web.Components.Search;

/// <summary>
/// "This folder" / "Entire share" inside a virtual share (/files/access/{id}/...). Virtual
/// shares are not indexed, so these scopes walk the remote tree by name through the browser's
/// own view model (same circuit, same loaded share) instead of asking the search index.
/// </summary>
public partial class GlobalSearch
{
    [Inject] private IServiceProvider Services { get; set; } = default!;

    // Resolved on use only: the circuit's virtual-share browser, never built on other pages.
    private RemoteCloudAccessFileBrowserViewModel VirtualShareBrowser
        => Services.GetRequiredService<RemoteCloudAccessFileBrowserViewModel>();

    // Below the 10 s search timeout, so a slow provider still shows what was found.
    private static readonly TimeSpan VirtualShareBudget = TimeSpan.FromSeconds(8);

    // The virtual share of the current route, and the one the shown hits came from.
    private Guid? _virtualShareId;
    private Guid? _hitsVirtualShareId;

    private bool IsVirtualShareScope => _virtualShareId != null && _scope is ScopeFolder or ScopeShare;

    /// <summary>Picks up /files/access/{id}/{sub...}; false for anything else under /files/access.</summary>
    private bool TryDetectVirtualShare(string[] segs)
    {
        if (segs.Length < 3 || !Guid.TryParse(segs[2], out var id))
            return false;
        _virtualShareId = id;
        _contextSubPath = string.Join('/', segs.Skip(3).Select(Uri.UnescapeDataString));
        return true;
    }

    // Display name of the context share; a virtual share's name is known once its browser has loaded it.
    private string ContextShareLabel => _virtualShareId is { } id
        ? VirtualShareBrowser.CurrentBrowserShare is { } shown && shown.Id == id ? shown.Name : ""
        : _contextShare ?? "";

    private async Task<List<FileDocument>> SearchVirtualShareAsync(Guid shareId, CancellationToken token)
    {
        var from = _scope == ScopeFolder ? _contextSubPath : "";
        var hits = await VirtualShareBrowser.SearchNamesAsync(
            shareId, from, _searchQuery, MaxShownFileHits, VirtualShareBudget, token);
        var shareName = ContextShareLabel;
        return hits.Select(h => new FileDocument
        {
            Id = h.Path,
            FileName = h.Name,
            ShareName = shareName,
            AbsolutePath = h.Path,
            SharePath = h.Path,
            FileType = h.IsDirectory ? string.Empty : Path.GetExtension(h.Name).TrimStart('.'),
            FileSizeBytes = h.Size,
            IsDirectory = h.IsDirectory
        }).ToList();
    }
}
