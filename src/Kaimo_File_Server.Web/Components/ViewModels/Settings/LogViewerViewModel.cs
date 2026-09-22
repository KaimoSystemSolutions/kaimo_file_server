using Kaimo_File_Server.Core.Logging;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public sealed class LogViewerViewModel(
    ILogArchiveReader reader,
    LogDownloadTokenService downloadTokens,
    AuthenticationStateProvider authenticationState,
    IUserContextFactory userContextFactory,
    IManagementAuthService managementAuth,
    ILogger<LogViewerViewModel> logger)
{
    public const int MaxExcludedMessagePrefixes = LogArchiveQueryLimits.MaxExcludedMessagePrefixes;
    public const int MaxExcludedMessagePrefixLength = LogArchiveQueryLimits.MaxExcludedMessagePrefixLength;

    // One rendered page is kept small so every filter change and 3-second live
    // tick re-diffs only a modest Blazor Server render tree; older entries are
    // reached by paging. The reader hard-caps at 1,000 rows, so paging is bounded
    // to MaxPages. Full history stays available through the download.
    public const int PageSize = 100;
    private const int MaxFetch = 1000;
    public static int MaxPages => MaxFetch / PageSize;

    private readonly SemaphoreSlim _queryGate = new(1, 1);
    private readonly List<string> _excludedMessagePrefixes = [];

    public IReadOnlyList<string> Sources { get; private set; } = [];
    public HashSet<string> SelectedSources { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Everything fetched so far (newest first); the visible page is the current
    // slice of it. Paging forward fetches deeper, paging back reuses what is
    // already in hand.
    private List<LogArchiveEntry> _fetched = [];
    private bool _moreBeyondFetched;
    public int CurrentPage { get; private set; } = 1;
    public ICollection<LogArchiveEntry> Entries =>
        _fetched.Skip((CurrentPage - 1) * PageSize).Take(PageSize).ToArray();
    public bool HasPreviousPage => CurrentPage > 1;
    public bool HasNextPage =>
        _fetched.Count > CurrentPage * PageSize
        || (_moreBeyondFetched && CurrentPage < MaxPages);
    // The 1,000-row reader cap is reached while entries are still being truncated.
    public bool ReachedPageCeiling => _moreBeyondFetched && CurrentPage >= MaxPages;

    // Which levels can be toggled in the viewer, and which are currently shown.
    // Mirrors the source filter exactly (a HashSet + Set… + checked/@onchange),
    // which is the selection pattern that reliably drives the query here.
    private static readonly LogLevel[] SelectableLevels =
        [LogLevel.Information, LogLevel.Warning, LogLevel.Error, LogLevel.Critical];
    public IReadOnlyList<LogLevel> Levels => SelectableLevels;
    // Information is the high-volume firehose and is off by default; the initial
    // load shows only Warning and above. Users can enable it via the level filter.
    public HashSet<LogLevel> SelectedLevels { get; } =
        [LogLevel.Warning, LogLevel.Error, LogLevel.Critical];

    public void SetLevelSelected(LogLevel level, bool selected)
    {
        if (!SelectableLevels.Contains(level))
            return;
        if (selected)
            SelectedLevels.Add(level);
        else
            SelectedLevels.Remove(level);
    }

    public string SearchText { get; set; } = "";
    public IReadOnlyList<string> ExcludedMessagePrefixes => _excludedMessagePrefixes;
    // Scopes both the visible list and the download to a single UTC day. Defaults
    // to today (the common case, and it keeps downloads bounded); null = all days.
    public DateOnly? FilterDateUtc { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public bool IsLivePaused { get; set; }
    public bool IsLoading { get; private set; }
    public bool IsAuthorized { get; private set; }
    public string? ErrorMessage { get; private set; }

    // Cheap identity of the current result so the live loop can skip a re-render
    // when nothing new arrived (the append-only tail changes its newest row).
    public (long Sequence, int Count) TopSignature
        => (_fetched.Count == 0 ? 0 : _fetched[0].Sequence, _fetched.Count);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        IsAuthorized = await AuthorizeAsync();
        if (!IsAuthorized)
            return;

        Sources = await reader.GetSourcesAsync(cancellationToken);
        SelectedSources.Clear();
        SelectedSources.UnionWith(Sources);
        await RefreshAsync(cancellationToken);
    }

    public void SetSourceSelected(string source, bool selected)
    {
        if (!Sources.Contains(source, StringComparer.OrdinalIgnoreCase))
            return;
        if (selected)
            SelectedSources.Add(source);
        else
            SelectedSources.Remove(source);
    }

    public bool TryAddExcludedMessagePrefix(string? value)
    {
        var prefix = value?.Trim();
        if (string.IsNullOrEmpty(prefix) || _excludedMessagePrefixes.Count >= MaxExcludedMessagePrefixes)
            return false;

        prefix = prefix[..Math.Min(prefix.Length, MaxExcludedMessagePrefixLength)];
        if (_excludedMessagePrefixes.Contains(prefix, StringComparer.OrdinalIgnoreCase))
            return false;

        _excludedMessagePrefixes.Add(prefix);
        return true;
    }

    public bool RemoveExcludedMessagePrefix(string prefix)
    {
        var index = _excludedMessagePrefixes.FindIndex(
            candidate => string.Equals(candidate, prefix, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return false;
        _excludedMessagePrefixes.RemoveAt(index);
        return true;
    }

    // Live polling skips a tick when a query is already running; a user
    // action (filter change, exclusion, manual refresh) must not be dropped,
    // so it waits its turn instead.
    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        // A filter change or manual refresh always returns to the first page.
        CurrentPage = 1;
        return RefreshCoreAsync(waitForGate: true, cancellationToken);
    }

    public Task LiveRefreshAsync(CancellationToken cancellationToken = default)
        => RefreshCoreAsync(waitForGate: false, cancellationToken);

    public Task GoToPageAsync(int page, CancellationToken cancellationToken = default)
    {
        page = Math.Clamp(page, 1, MaxPages);
        if (page == CurrentPage)
            return Task.CompletedTask;
        // Paging back reuses rows already fetched; only paging past them re-queries.
        var needsFetch = page * PageSize > _fetched.Count;
        CurrentPage = page;
        return needsFetch ? RefreshCoreAsync(waitForGate: true, cancellationToken) : Task.CompletedTask;
    }

    private async Task RefreshCoreAsync(bool waitForGate, CancellationToken cancellationToken)
    {
        if (!IsAuthorized)
            return;
        if (waitForGate)
        {
            try { await _queryGate.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { return; }
        }
        else if (!await _queryGate.WaitAsync(0, cancellationToken))
        {
            return;
        }
        try
        {
            IsLoading = true;
            ErrorMessage = null;
            var result = await reader.QueryAsync(CreateQuery(), cancellationToken);
            _fetched = result.Entries.ToList();
            _moreBeyondFetched = result.HasMore;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Log archive query failed");
            ErrorMessage = Resources.Web_Settings_LogViewer_ReadFailed;
        }
        finally
        {
            IsLoading = false;
            _queryGate.Release();
        }
    }

    // format: "log" for the human-readable text export, otherwise raw NDJSON.
    public string CreateDownloadUrl(string baseUri, string format)
    {
        if (!IsAuthorized)
            return "";
        var token = Uri.EscapeDataString(downloadTokens.Protect(CreateQuery()));
        return $"{baseUri.TrimEnd('/')}/api/system-logs/download?token={token}&format={Uri.EscapeDataString(format)}";
    }

    private LogArchiveQuery CreateQuery()
        => new(
            SelectedSources.ToArray(),
            SearchText: SearchText,
            // Over-fetch to the end of the current page; Entries slices the page,
            // and the extra row the reader adds signals whether a next page exists.
            Limit: Math.Min(CurrentPage * PageSize, MaxFetch),
            UtcDate: FilterDateUtc,
            ExcludedMessagePrefixes: _excludedMessagePrefixes.ToArray(),
            Levels: SelectedLevels.ToArray());

    private async Task<bool> AuthorizeAsync()
    {
        var state = await authenticationState.GetAuthenticationStateAsync();
        var username = state.User.Identity?.Name;
        if (string.IsNullOrEmpty(username))
            return false;
        var actor = await userContextFactory.CreateByUsernameAsync(username);
        return actor is not null
            && await managementAuth.HasGlobalPermissionAsync(actor, ManagementPermission.ViewSystemLogs);
    }
}
