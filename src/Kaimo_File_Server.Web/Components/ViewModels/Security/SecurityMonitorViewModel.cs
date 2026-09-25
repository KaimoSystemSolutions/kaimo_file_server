using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>
/// Security overview: login attempts per account and API / WebDAV traffic per client address
/// for a selectable period, read from the persisted security events. Gated by
/// <see cref="ManagementPermission.ViewSecurityMonitor"/>.
/// </summary>
public sealed class SecurityMonitorViewModel(
    SecurityMonitor monitor,
    ISecurityEventRepository events,
    IUserRepository users,
    IManagementAuthService mgmtAuth,
    IUserContextFactory userContexts,
    AuthenticationStateProvider authState,
    IConfiguration configuration,
    ILogger<SecurityMonitorViewModel> logger)
{
    /// <summary>Maximum rows per list; the headline numbers always cover the whole period.</summary>
    public const int ListLimit = 500;

    /// <summary>Selectable periods in days.</summary>
    public static readonly int[] PeriodOptions = [1, 7, 30, 365];

    public bool IsLoading { get; private set; }
    public bool CanAccessPage { get; private set; }
    public string? ErrorMessage { get; private set; }

    public int PeriodDays { get; private set; } = 7;
    public int RetentionDays => SecurityMonitorFlushService.RetentionDays(configuration);

    public SecurityTotals Totals { get; private set; } = new(0, 0, 0, 0, 0);
    public IReadOnlyList<AccountLoginSummary> Accounts { get; private set; } = [];
    public IReadOnlyList<ClientActivitySummary> Clients { get; private set; } = [];
    public IReadOnlyList<LoginAttemptRecord> Attempts { get; private set; } = [];
    public IReadOnlySet<string> KnownAccounts { get; private set; } = new HashSet<string>();

    public string AttemptSearch { get; private set; } = "";
    public bool FailedOnly { get; private set; }

    public event Action? OnStateChanged;
    private void Notify() => OnStateChanged?.Invoke();

    private DateTime SinceUtc => DateTime.UtcNow.AddDays(-PeriodDays);

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var state = await authState.GetAuthenticationStateAsync();
            var username = state.User.Identity?.Name;
            var actor = string.IsNullOrEmpty(username) ? null : await userContexts.CreateByUsernameAsync(username);
            CanAccessPage = actor is not null
                            && await mgmtAuth.HasAnyPermissionAsync(actor, ManagementPermission.ViewSecurityMonitor);
            if (!CanAccessPage) return;

            KnownAccounts = (await users.GetAllAsync())
                .Select(u => u.Username.ToLowerInvariant())
                .ToHashSet();
            await QueryAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = Core.Language.Resources.Web_Error_LoadFilesFailed;
            logger.LogError(ex, "Failed to load the security overview");
        }
        finally
        {
            IsLoading = false;
            Notify();
        }
    }

    public Task RefreshAsync() => RunAsync(QueryAsync);

    public Task SetPeriodAsync(int days)
    {
        PeriodDays = PeriodOptions.Contains(days) ? days : 7;
        return RunAsync(QueryAsync);
    }

    public Task SetAttemptFilterAsync(string search, bool failedOnly)
    {
        AttemptSearch = search;
        FailedOnly = failedOnly;
        return RunAsync(QueryAttemptsAsync);
    }

    private async Task QueryAsync()
    {
        // Include what was recorded since the last background flush.
        await monitor.FlushAsync(events);

        var now = DateTime.UtcNow;
        Totals = await events.GetTotalsAsync(SinceUtc, now);
        Accounts = await events.SummarizeAccountsAsync(SinceUtc, now, ListLimit);
        Clients = await events.SummarizeClientsAsync(SinceUtc, ListLimit);
        await QueryAttemptsAsync();
    }

    private async Task QueryAttemptsAsync()
        => Attempts = await events.ListLoginAttemptsAsync(SinceUtc, AttemptSearch, FailedOnly, ListLimit);

    private async Task RunAsync(Func<Task> query)
    {
        if (!CanAccessPage) return;
        ErrorMessage = null;
        try
        {
            await query();
        }
        catch (Exception ex)
        {
            ErrorMessage = Core.Language.Resources.Web_Error_LoadFilesFailed;
            logger.LogError(ex, "Failed to query the security overview");
        }
        Notify();
    }
}
