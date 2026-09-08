using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Search;
using Kaimo_File_Server.Web.Components.Shared;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;

namespace Kaimo_File_Server.Web.Components.Search;

/// <summary>
/// The global search bar shown on every page (extracted from MainLayout). It searches
/// two things at once — navigation destinations (tabs/settings) and files — with the
/// scope chosen from a single dropdown next to the input.
///
/// <para>Inside the file browser the options are: this folder (default), the whole
/// share, all shares, or settings/tabs. Outside the browser: settings/tabs (default),
/// all shares, or "selected shares" — the last reveals the shared
/// <see cref="PrincipalPickerField"/> (the same multi-select share dialog used in the
/// department editor).</para>
///
/// <para>Any file scope shows file hits with a small "settings &amp; tabs" section
/// beneath; the settings scope shows destinations only. All access control is delegated
/// to code that already enforces it: file hits go through <see cref="ISearchService"/>
/// (mandatory ACL filter), destinations/shares via <see cref="IManagementAuthService"/>
/// / <see cref="IAclService"/>.</para>
/// </summary>
public partial class GlobalSearch : IDisposable
{
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private ISearchService SearchService { get; set; } = default!;
    [Inject] private IUserContextFactory UserContextFactory { get; set; } = default!;
    [Inject] private IManagementAuthService ManagementAuth { get; set; } = default!;
    [Inject] private AuthenticationStateProvider AuthStateProvider { get; set; } = default!;
    [Inject] private FileSelectionCoordinator FileSelectionCoordinator { get; set; } = default!;
    [Inject] private IShareRepository ShareRepo { get; set; } = default!;
    [Inject] private IAclService Acl { get; set; } = default!;

    //  "dest"   → settings/tabs only
    //  "global" → all shares (file search)
    //  "folder" → current folder + below (browser only)
    //  "share"  → the current share (browser only)
    //  "pick"   → selected shares chosen via the picker (outside the browser)
    private const string ScopeDest = "dest";
    private const string ScopeGlobal = "global";
    private const string ScopeFolder = "folder";
    private const string ScopeShare = "share";
    private const string ScopePick = "pick";

    private string _searchQuery = "";
    private string _scope = ScopeDest;
    private string _lastScope = ScopeDest;   // last non-"pick" scope, to revert to
    private ElementReference _searchInput;

    private List<FileDocument> _fileResults = new();
    private List<SearchDestination> _destResults = new();
    private bool _dropdownVisible;
    private bool _isSearching;
    private CancellationTokenSource? _searchCts;

    private UserContext? _user;

    // Context, refreshed on every navigation.
    private bool _inBrowser;
    private string? _contextShare;
    private string _contextSubPath = "";

    // Shares the user may read (outside the browser) and the committed selection.
    private List<(Guid Id, string Name)>? _visibleShares;
    private List<(Guid Id, string Name)> _pickedShares = new();

    // Open state of the shared share-picker dialog (driven by us, not its trigger).
    private bool _pickerOpen;

    // Projections for the shared PrincipalPickerField (same dialog as the dept editor).
    private IReadOnlyList<PrincipalPickerItem> ShareItems =>
        (_visibleShares ?? new())
        .Select(s => new PrincipalPickerItem(s.Id, s.Name, PrincipalKind.Share))
        .ToList();

    private IReadOnlyCollection<Guid> PickedShareIds => _pickedShares.Select(s => s.Id).ToList();

    protected override async Task OnInitializedAsync()
    {
        Nav.LocationChanged += OnLocationChanged;

        var state = await AuthStateProvider.GetAuthenticationStateAsync();
        var username = state.User.Identity?.Name;
        if (!string.IsNullOrEmpty(username))
            _user = await UserContextFactory.CreateByUsernameAsync(username);

        DetectContext();
        if (!_inBrowser)
            await LoadVisibleSharesAsync();
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
        => _ = InvokeAsync(async () =>
        {
            var wasInBrowser = _inBrowser;
            DetectContext();
            ResetResults();
            _searchQuery = "";
            _pickedShares.Clear();
            if (!_inBrowser && (_visibleShares is null || wasInBrowser))
                await LoadVisibleSharesAsync();
            StateHasChanged();
        });

    /// <summary>
    /// Detects the file-browser context from the URL and resets the scope to the
    /// context's sensible default. The GUID cloud-access route (/files/access/...) is
    /// treated as "in browser but no name-scopable share" → global.
    /// </summary>
    private void DetectContext()
    {
        var rel = Nav.ToBaseRelativePath(Nav.Uri);
        var path = rel.Split('?', '#')[0].Trim('/');
        var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segs.Length == 0 || !segs[0].Equals("files", StringComparison.OrdinalIgnoreCase))
        {
            _inBrowser = false;
            _contextShare = null;
            _contextSubPath = "";
            _scope = _lastScope = ScopeDest;
            return;
        }

        _inBrowser = true;
        if (segs.Length < 2 || segs[1].Equals("access", StringComparison.OrdinalIgnoreCase))
        {
            _contextShare = null;
            _contextSubPath = "";
            _scope = _lastScope = ScopeGlobal;
            return;
        }

        _contextShare = Uri.UnescapeDataString(segs[1]);
        _contextSubPath = segs.Length > 2
            ? string.Join('/', segs.Skip(2).Select(Uri.UnescapeDataString))
            : "";
        _scope = _lastScope = ScopeFolder;
    }

    /// <summary>Scope options for the current context, as (value, label) pairs.</summary>
    private IEnumerable<(string Value, string Label)> ScopeOptions()
    {
        if (_inBrowser && _contextShare != null)
        {
            yield return (ScopeFolder, Text("Web_Search_Scope_Folder", "This folder"));
            yield return (ScopeShare, Text("Web_Search_Scope_Share", "Entire share"));
            yield return (ScopeGlobal, Text("Web_Search_AllShares", "All shares"));
            yield return (ScopeDest, Text("Web_Search_Scope_Settings", "Settings"));
            yield break;
        }

        if (_inBrowser)
        {
            yield return (ScopeGlobal, Text("Web_Search_AllShares", "All shares"));
            yield return (ScopeDest, Text("Web_Search_Scope_Settings", "Settings"));
            yield break;
        }

        yield return (ScopeDest, Text("Web_Search_Scope_Settings", "Settings"));
        yield return (ScopeGlobal, Text("Web_Search_AllShares", "All shares"));
        yield return (ScopePick, Text("Web_Search_Scope_PickedShare", "Selected shares"));
    }

    /// <summary>
    /// The shares the user may read. Mirrors ShareListViewModel: a share-manager sees
    /// managed shares regardless of state; everyone else needs an enabled, non-hidden
    /// share with ListReadData on its root.
    /// </summary>
    private async Task LoadVisibleSharesAsync()
    {
        if (_user is null)
        {
            _visibleShares = new List<(Guid, string)>();
            return;
        }

        var mgmtScope = await ManagementAuth.GetAuthorizedShareIdsAnyAsync(
            _user, ManagementPermission.ShareAdmin);
        var managedIds = mgmtScope.IsUnrestricted ? null : mgmtScope.ScopeIds.ToHashSet();

        var found = new List<(Guid Id, string Name)>();
        foreach (var share in await ShareRepo.GetAllAsync())
        {
            if (mgmtScope.IsUnrestricted || managedIds!.Contains(share.Id))
            {
                found.Add((share.Id, share.Name));
                continue;
            }
            if (!share.IsEnabled || share.IsShareHidden)
                continue;
            if (await Acl.HasAccessAsync(_user, share.Id, "", true, FilePermission.ListReadData))
                found.Add((share.Id, share.Name));
        }

        _visibleShares = found.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();

        var visibleIds = _visibleShares.Select(s => s.Id).ToHashSet();
        _pickedShares.RemoveAll(s => !visibleIds.Contains(s.Id));
    }

    /// <summary>Commit from the shared picker dialog (same contract as the dept editor).</summary>
    private async Task OnSharesPicked(IReadOnlyList<Guid> ids)
    {
        var byId = (_visibleShares ?? new()).ToDictionary(s => s.Id, s => s.Name);
        _pickedShares = ids
            .Where(byId.ContainsKey)
            .Select(id => (id, byId[id]))
            .ToList();
        await RunSearchNow();
    }

    private string PickedSharesTitle => string.Join(", ", _pickedShares.Select(s => s.Name));

    // ── Search ──

    private async Task OnSearchInputChanged()
    {
        if (_searchQuery.Length < 2)
        {
            ResetResults();
            _dropdownVisible = false;
            return;
        }

        var token = NewSearchToken();
        _isSearching = true;
        _dropdownVisible = true;
        try
        {
            await Task.Delay(250, token); // debounce
            await ExecuteSearchAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task OnScopeChanged()
    {
        if (_scope == ScopePick)
        {
            // Selecting the option opens the picker automatically the first time;
            // afterwards the info button reopens it.
            _pickerOpen = true;
        }
        else
        {
            // A base scope: remember it (to revert to) and clear the share filter.
            _lastScope = _scope;
            _pickedShares.Clear();
        }

        await RunSearchNow();
    }

    /// <summary>
    /// Fired when the share dialog closes. With nothing selected the dropdown springs
    /// back to the last base scope; either way the results dropdown is kept open (we
    /// return focus to the input so the focus-out auto-hide doesn't collapse it).
    /// </summary>
    private async Task OnPickerOpenChanged(bool open)
    {
        _pickerOpen = open;
        if (open)
            return;

        if (_pickedShares.Count == 0)
            _scope = _lastScope;

        await RunSearchNow();
        await RefocusSearchAsync();
    }

    private async Task RefocusSearchAsync()
    {
        // Returning focus makes the component "focus-within" again, cancelling the
        // pending focus-out hide so the dropdown stays open after the dialog closes.
        try { await _searchInput.FocusAsync(); } catch { /* element may be gone */ }
    }

    private string SharesSelectedTitle =>
        string.Format(Text("Web_Search_SharesSelected", "{0} shares selected"), _pickedShares.Count);

    private async Task RunSearchNow()
    {
        if (_searchQuery.Length < 2)
        {
            ResetResults();
            _dropdownVisible = false;
            return;
        }

        var token = NewSearchToken();
        _isSearching = true;
        _dropdownVisible = true;
        try
        {
            await ExecuteSearchAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private CancellationToken NewSearchToken()
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        return _searchCts.Token;
    }

    private async Task ExecuteSearchAsync(CancellationToken token)
    {
        if (_user is null)
        {
            ResetResults();
            _isSearching = false;
            return;
        }

        var plan = ResolvePlan();

        var files = new List<FileDocument>();
        foreach (var (share, sub) in plan.Targets)
        {
            var hits = await SearchService.SearchAsync(_searchQuery, _user, share, sub, token);
            files.AddRange(hits);
        }

        var dests = plan.Dest
            ? await SearchDestinations.MatchAsync(_searchQuery, ManagementAuth, _user, token)
            : new List<SearchDestination>();

        if (token.IsCancellationRequested)
            return;

        _fileResults = files
            .GroupBy(f => f.Id)
            .Select(g => g.First())
            .Take(8)
            .ToList();
        _destResults = dests.Take(plan.DestLimit).ToList();
        _isSearching = false;
    }

    private (bool Dest, int DestLimit, List<(string? Share, string Sub)> Targets) ResolvePlan()
    {
        if (_scope == ScopeDest)
            return (true, 8, new List<(string?, string)>());

        if (_scope == ScopeGlobal)
            return (true, 3, new List<(string?, string)> { (null, "") });

        if (_scope == ScopeFolder && _contextShare != null)
            return (true, 3, new List<(string?, string)> { (_contextShare, _contextSubPath) });

        if (_scope == ScopeShare && _contextShare != null)
            return (true, 3, new List<(string?, string)> { (_contextShare, "") });

        if (_scope == ScopePick && _pickedShares.Count > 0)
            return (true, 3, _pickedShares.Select(s => ((string?)s.Name, "")).ToList());

        // Pick scope with nothing chosen (or an unresolvable share): settings only.
        return (true, 8, new List<(string?, string)>());
    }

    // ── Navigation ──

    private void NavigateToFile(FileDocument result)
    {
        HideAndClear();

        string url;
        if (result.IsDirectory)
        {
            var dirPath = (result.SharePath ?? string.Empty).Replace('\\', '/').Trim('/');
            url = string.IsNullOrEmpty(dirPath) || dirPath == "."
                ? $"/files/{result.ShareName}"
                : $"/files/{result.ShareName}/{dirPath}";
        }
        else
        {
            var folderPath = (Path.GetDirectoryName(result.SharePath) ?? string.Empty)
                .Replace('\\', '/').Trim('/');
            url = string.IsNullOrEmpty(folderPath)
                ? $"/files/{result.ShareName}"
                : $"/files/{result.ShareName}/{folderPath}";

            if (FileSelectionCoordinator.RequestSelection(result.ShareName, folderPath, result.FileName))
                return;
        }

        Nav.NavigateTo(url);
    }

    private void NavigateToDestination(SearchDestination dest)
    {
        HideAndClear();
        Nav.NavigateTo(dest.Url);
    }

    private void HandleSearchKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Escape")
            _dropdownVisible = false;
    }

    // Focus-within tracking so moving focus between the input and the scope picker
    // keeps the dropdown open; leaving the component closes it.
    private bool _focusWithin;

    private void OnFocusIn()
    {
        _focusWithin = true;
        if (HasResults || _isSearching)
            _dropdownVisible = true;
    }

    private async Task OnFocusOut()
    {
        _focusWithin = false;
        await Task.Delay(200);
        if (!_focusWithin)
        {
            _dropdownVisible = false;
            StateHasChanged();
        }
    }

    private void ClearSearch() => HideAndClear();

    private static string Text(string key, string fallback)
        => Resources.ResourceManager.GetString(key) ?? fallback;

    /// <summary>A short "where am I searching" hint shown inside the bar while typing.</summary>
    private string ScopeHint()
    {
        if (_scope == ScopePick)
            return _pickedShares.Count > 0
                ? $"{Text("Web_Search_Hint_In", "Searching in:")} {PickedSharesTitle}"
                : Text("Web_Search_Scope_PickedShare", "Selected shares");

        if (_scope == ScopeDest)
            return Text("Web_Search_Hint_Settings", "Searching settings & tabs");
        if (_scope == ScopeGlobal)
            return Text("Web_Search_Hint_AllShares", "Searching all shares");

        string share;
        var sub = "";
        if (_scope == ScopeFolder)
        {
            share = _contextShare ?? "";
            sub = _contextSubPath;
        }
        else if (_scope == ScopeShare)
        {
            share = _contextShare ?? "";
        }
        else
        {
            return "";
        }

        if (string.IsNullOrEmpty(share))
            return Text("Web_Search_Scope_Settings", "Settings");

        var location = string.IsNullOrEmpty(sub) ? share : $"{share}/{sub}";
        return $"{Text("Web_Search_Hint_In", "Searching in:")} {location}";
    }

    private void HideAndClear()
    {
        _dropdownVisible = false;
        _searchQuery = "";
        ResetResults();
    }

    private void ResetResults()
    {
        _fileResults = new();
        _destResults = new();
        _isSearching = false;
    }

    private bool HasResults => _fileResults.Count > 0 || _destResults.Count > 0;

    public void Dispose()
    {
        Nav.LocationChanged -= OnLocationChanged;
        _searchCts?.Cancel();
        _searchCts?.Dispose();
    }
}
