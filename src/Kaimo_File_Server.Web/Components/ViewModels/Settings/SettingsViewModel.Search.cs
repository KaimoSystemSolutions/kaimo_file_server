using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Search;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>Search-engine (Elasticsearch) state and manual reindex control.</summary>
public partial class SettingsViewModel
{
    // ── Search engine (Elasticsearch) ──

    /// <summary>Desired state of Elasticsearch (config flag). When off, the
    /// filename fallback is used and nothing is indexed on write.</summary>
    public bool SearchEsEnabled { get; set; } = true;

    /// <summary>Whether Elasticsearch answered a ping (container present/reachable).</summary>
    public bool SearchEsReachable { get; private set; }

    /// <summary>True if ES is both enabled and reachable → actually in use.</summary>
    public bool SearchEsEffective { get; private set; }

    /// <summary>True while the search tab is actively probing Elasticsearch.</summary>
    public bool SearchStateLoading { get; private set; }

    /// <summary>Progress of the last/running manual reindex.</summary>
    public ReindexProgress ReindexProgress { get; private set; } = ReindexProgress.Idle;

    /// <summary>Enabled share names available for a scoped reindex.</summary>
    public List<string> ReindexShares { get; private set; } = new();

    /// <summary>Selected share for the next reindex; empty = all shares.</summary>
    public string ReindexSelectedShare { get; set; } = string.Empty;

    /// <summary>Reads the desired flag + live reachability of Elasticsearch.</summary>
    public async Task LoadSearchStateAsync()
    {
        if (!CanManageSettings) return;
        SearchStateLoading = true;
        try
        {
            var state = await _searchAdmin.GetStateAsync();
            SearchEsEnabled = state.Enabled;
            SearchEsReachable = state.Reachable;
            SearchEsEffective = state.Effective;
            ReindexProgress = _searchAdmin.GetReindexProgress();

            ReindexShares = (await _shareRepo.GetAllEnabledAsync())
                .Select(s => s.Name)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            // Drop a stale selection if that share is gone.
            if (!ReindexShares.Contains(ReindexSelectedShare))
                ReindexSelectedShare = string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load search engine state");
        }
        finally
        {
            SearchStateLoading = false;
        }
    }

    /// <summary>Re-reads only the reindex progress (cheap, in-memory).</summary>
    public Task RefreshReindexProgressAsync()
    {
        if (CanManageSettings)
            ReindexProgress = _searchAdmin.GetReindexProgress();
        return Task.CompletedTask;
    }

    /// <summary>Requests cancellation of the running reindex. The run stops at its
    /// next file/directory boundary; the poll reflects the canceled state.</summary>
    public void CancelReindex()
    {
        if (!CanManageSettings) return;
        _searchAdmin.CancelReindex();
        ReindexProgress = _searchAdmin.GetReindexProgress();
    }

    public async Task<bool> SaveSearchAsync()
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (!CanManageSettings)
        {
            ErrorMessage = Resources.Web_Settings_NoPermissionChange;
            return false;
        }

        try
        {
            await _searchAdmin.SetElasticEnabledAsync(SearchEsEnabled);
            await LoadSearchStateAsync();

            _logger.LogInformation("Elasticsearch desired state set to {Enabled}", SearchEsEnabled);
            SuccessMessage = SearchEsEnabled
                ? "Elasticsearch aktiviert. Wird verwendet, sobald der Container erreichbar ist."
                : "Elasticsearch deaktiviert. Es wird die Dateinamen-Suche verwendet und nicht mehr indexiert.";
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save search engine setting");
            ErrorMessage = "Such-Einstellung konnte nicht gespeichert werden.";
            return false;
        }
    }

    /// <summary>Triggers a manual reindex — of all files on disk, or just the
    /// files in <see cref="ReindexSelectedShare"/> when one is selected.</summary>
    public async Task StartReindexAsync()
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (!CanManageSettings)
        {
            ErrorMessage = Resources.Web_Settings_NoPermissionChange;
            return;
        }

        try
        {
            var share = string.IsNullOrEmpty(ReindexSelectedShare) ? null : ReindexSelectedShare;
            var started = await _searchAdmin.TryStartReindexAsync(share);
            ReindexProgress = _searchAdmin.GetReindexProgress();

            if (started)
                SuccessMessage = share is null
                    ? "Indexierung gestartet. Der Fortschritt wird unten angezeigt."
                    : $"Indexierung für Share '{share}' gestartet. Der Fortschritt wird unten angezeigt.";
            else if (ReindexProgress.Running)
                ErrorMessage = "Es läuft bereits eine Indexierung.";
            else
                ErrorMessage = "Indexierung nicht möglich: Elasticsearch ist deaktiviert oder nicht erreichbar.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start reindex");
            ErrorMessage = "Indexierung konnte nicht gestartet werden.";
        }
    }
}
