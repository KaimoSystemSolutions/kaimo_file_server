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

    private readonly SemaphoreSlim _queryGate = new(1, 1);
    private readonly List<string> _excludedMessagePrefixes = [];

    public IReadOnlyList<string> Sources { get; private set; } = [];
    public HashSet<string> SelectedSources { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ICollection<LogArchiveEntry> Entries { get; private set; } = [];
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;
    public string SearchText { get; set; } = "";
    public IReadOnlyList<string> ExcludedMessagePrefixes => _excludedMessagePrefixes;
    public DateOnly DownloadDateUtc { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public bool IsLivePaused { get; set; }
    public bool HasMore { get; private set; }
    public bool IsLoading { get; private set; }
    public bool IsAuthorized { get; private set; }
    public string? ErrorMessage { get; private set; }

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
        => RefreshCoreAsync(waitForGate: true, cancellationToken);

    public Task LiveRefreshAsync(CancellationToken cancellationToken = default)
        => RefreshCoreAsync(waitForGate: false, cancellationToken);

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
            Entries = result.Entries.ToArray();
            HasMore = result.HasMore;
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

    public string CreateDownloadUrl(string baseUri)
    {
        if (!IsAuthorized)
            return "";
        var token = Uri.EscapeDataString(downloadTokens.Protect(CreateQuery(DownloadDateUtc)));
        return $"{baseUri.TrimEnd('/')}/api/system-logs/download?token={token}";
    }

    private LogArchiveQuery CreateQuery(DateOnly? utcDate = null)
        => new(
            SelectedSources.ToArray(),
            MinimumLevel,
            SearchText,
            1000,
            utcDate,
            _excludedMessagePrefixes.ToArray());

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
