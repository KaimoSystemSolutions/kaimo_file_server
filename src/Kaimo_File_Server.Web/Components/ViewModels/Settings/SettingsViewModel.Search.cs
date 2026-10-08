using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Search;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>Search-engine selection (Elasticsearch / local index / filename) and manual reindex control.</summary>
public partial class SettingsViewModel
{
    // ── Search engine ──

    /// <summary>Desired engine (config). Persisted on Save; Filename means no index at all.</summary>
    public SearchEngine SearchEngineSelected { get; set; } = SearchEngine.Elasticsearch;

    /// <summary>Desired state of Elasticsearch. When off, nothing is written to Elasticsearch.</summary>
    public bool SearchEsEnabled => SearchEngineSelected == SearchEngine.Elasticsearch;

    /// <summary>Desired state of the local (PostgreSQL) index.</summary>
    public bool SearchLocalEnabled => SearchEngineSelected == SearchEngine.Local;

    /// <summary>Whether Elasticsearch answered a ping (container present/reachable).</summary>
    public bool SearchEsReachable { get; private set; }

    /// <summary>The engine actually serving searches right now (Filename when the selected one is unavailable).</summary>
    public SearchEngine SearchActiveEngine { get; private set; } = SearchEngine.Filename;

    /// <summary>True if ES is both enabled and reachable → actually in use.</summary>
    public bool SearchEsEffective => SearchActiveEngine == SearchEngine.Elasticsearch;

    /// <summary>True if the local index is in use.</summary>
    public bool SearchLocalEffective => SearchActiveEngine == SearchEngine.Local;

    /// <summary>True when an index engine is in use, i.e. a reindex is possible.</summary>
    public bool SearchIndexEffective => SearchActiveEngine != SearchEngine.Filename;

    /// <summary>Size of the local index; null while it is not selected or could not be read.</summary>
    public SearchIndexStats? LocalIndexStats { get; private set; }

    /// <summary>True while the search tab is actively probing Elasticsearch.</summary>
    public bool SearchStateLoading { get; private set; }

    /// <summary>Progress of the last/running manual reindex.</summary>
    public ReindexProgress ReindexProgress { get; private set; } = ReindexProgress.Idle;

    /// <summary>Enabled share names available for a scoped reindex.</summary>
    public List<string> ReindexShares { get; private set; } = new();

    /// <summary>Selected share for the next reindex; empty = all shares.</summary>
    public string ReindexSelectedShare { get; set; } = string.Empty;

    /// <summary>
    /// Applies one engine toggle. The engines are mutually exclusive: switching one on switches
    /// the other off; switching the selected one off falls back to the filename search.
    /// Only the desired state changes; it is persisted on Save.
    /// </summary>
    public void ToggleSearchEngine(SearchEngine engine, bool on)
    {
        if (on)
            SearchEngineSelected = engine;
        else if (SearchEngineSelected == engine)
            SearchEngineSelected = SearchEngine.Filename;
    }

    /// <summary>Reads the selected engine, live reachability of Elasticsearch and the local index size.</summary>
    public async Task LoadSearchStateAsync()
    {
        if (!CanManageSettings) return;
        SearchStateLoading = true;
        try
        {
            var state = await _searchAdmin.GetStateAsync();
            SearchEngineSelected = state.Engine;
            // Elasticsearch is not probed while the local index is selected.
            SearchEsReachable = state.Engine != SearchEngine.Local && state.Reachable;
            SearchActiveEngine = state.Effective ? state.Engine : SearchEngine.Filename;
            ReindexProgress = _searchAdmin.GetReindexProgress();
            LocalIndexStats = state.Engine == SearchEngine.Local
                ? await _searchAdmin.GetLocalIndexStatsAsync()
                : null;

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

    /// <summary>
    /// Re-reads the reindex progress (cheap, in-memory). When a run has just finished, the
    /// local index size is re-read too, so the counts match the completed build.
    /// </summary>
    public async Task RefreshReindexProgressAsync()
    {
        if (!CanManageSettings) return;

        bool wasRunning = ReindexProgress.Running;
        ReindexProgress = _searchAdmin.GetReindexProgress();
        if (wasRunning && !ReindexProgress.Running && SearchLocalEffective)
        {
            try { LocalIndexStats = await _searchAdmin.GetLocalIndexStatsAsync(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to refresh local index stats"); }
        }
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
            var engine = SearchEngineSelected;
            await _searchAdmin.SetEngineAsync(engine);
            await LoadSearchStateAsync();

            _logger.LogInformation("Search engine set to {Engine}", engine);
            SuccessMessage = R(engine switch
            {
                SearchEngine.Elasticsearch => "Web_Settings_Search_SavedElastic",
                SearchEngine.Local => "Web_Settings_Search_SavedLocal",
                _ => "Web_Settings_Search_SavedFilename"
            });
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save search engine setting");
            ErrorMessage = R("Web_Settings_Search_SaveFailed");
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
                    ? R("Web_Settings_Search_ReindexStarted")
                    : string.Format(R("Web_Settings_Search_ReindexStartedShare"), share);
            else if (ReindexProgress.Running)
                ErrorMessage = R("Web_Settings_Search_ReindexAlreadyRunning");
            else
                ErrorMessage = R("Web_Settings_Search_ReindexUnavailable");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start reindex");
            ErrorMessage = R("Web_Settings_Search_ReindexStartFailed");
        }
    }
}
