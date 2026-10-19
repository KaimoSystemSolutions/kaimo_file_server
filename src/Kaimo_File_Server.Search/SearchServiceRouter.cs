using Elastic.Clients.Elasticsearch;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Logging;

namespace Kaimo_File_Server.Search;

/// <summary>
/// The <see cref="ISearchService"/> every process actually resolves. It routes
/// both searching and index-writing to the engine selected in the settings:
///
///  • <see cref="ElasticSearchService"/> when Elasticsearch is selected AND reachable
///    (the container exists and answers a ping).
///  • <see cref="LocalIndexSearchService"/> when local indexing is selected — the index lives
///    in the application's PostgreSQL database, so there is no reachability check.
///  • <see cref="FilenameSearchService"/> otherwise — a plain filename search that
///    needs no index, so writes are simply not indexed.
///
/// This makes the system degrade gracefully: bring up the stack without the
/// Elasticsearch container and search transparently falls back to filename
/// matching; switch the engine and indexing stops/starts within a few seconds.
///
/// It also implements <see cref="ISearchAdminService"/> for the settings page
/// (engine selection + manual reindex).
/// </summary>
public sealed partial class SearchServiceRouter : ISearchService, ISearchAdminService
{
    // How long an effective-state probe (engine read + ES ping) is reused before we
    // re-check. Bounds DB/ping load to roughly one probe per interval per process,
    // while keeping cross-process toggles reasonably responsive.
    private static readonly TimeSpan StateTtl = TimeSpan.FromSeconds(5);

    // Upper bound for a SINGLE reachability ping so an absent container can't stall
    // a write or a search for long.
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(5);

    // A single failed ping is not proof that ES is down. Under Docker/WSL2 the VM
    // clock can drift and stall Elasticsearch's timer thread for ~10s at a time
    // (logs: "timer thread slept for [11s]" / "absolute clock went backwards").
    // During such a stall a probe times out even though the cluster is green. We
    // retry a couple of times before declaring the container unreachable, so a
    // transient stall doesn't flip the settings page to "offline" and block reindex.
    private const int PingAttempts = 2;

    private readonly ElasticSearchService _es;
    private readonly LocalIndexSearchService _local;
    private readonly FilenameSearchService _filename;
    private readonly ElasticsearchClient _client;
    private readonly ISearchConfigStore _configStore;
    private readonly ILogger<SearchServiceRouter> _logger;

    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private SearchEngineState? _cachedState;
    private DateTime _stateExpiresUtc = DateTime.MinValue;
    private int _refreshing; // 1 while a background state probe runs
    // Edge-trigger for the "ES enabled but unreachable" warning: log once when it
    // goes down, then stay quiet until it recovers, so a persistently down cluster
    // doesn't spam a warning on every probe.
    private bool _esDownLogged;

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _esInitialized;

    private readonly object _reindexGate = new();
    private ReindexProgress _reindexProgress = ReindexProgress.Idle;
    private Task? _reindexTask;
    private CancellationTokenSource? _reindexCts;
    private SearchEngine _reindexEngine;

    public SearchServiceRouter(
        ElasticSearchService es,
        LocalIndexSearchService local,
        FilenameSearchService filename,
        ElasticsearchClient client,
        ISearchConfigStore configStore,
        ILogger<SearchServiceRouter> logger)
    {
        _es = es;
        _local = local;
        _filename = filename;
        _client = client;
        _configStore = configStore;
        _logger = logger;
    }

    // ══════════════════════════════════════════
    //  ISearchService — indexing hooks
    // ══════════════════════════════════════════

    public async Task onFileCreated(string absolutePath, Task<Stream> fileData, CancellationToken ct = default)
    {
        if (await GetActiveIndexAsync(ct) is { } index)
        {
            await index.onFileCreated(absolutePath, fileData, ct);
            return;
        }

        // Not indexing: the caller eagerly opened a read stream for us — release it
        // so we don't leak an OS file handle (which on Windows bind mounts blocks
        // later renames/moves of the file).
        await DrainAsync(fileData);
    }

    public async Task onFileDeleted(string absolutePath)
    {
        if (await GetActiveIndexAsync() is { } index)
            await index.onFileDeleted(absolutePath);
    }

    public async Task onDirectoryCreated(string absolutePath)
    {
        if (await GetActiveIndexAsync() is { } index)
            await index.onDirectoryCreated(absolutePath);
    }

    public async Task onDirectoryDeleted(string absolutePath)
    {
        if (await GetActiveIndexAsync() is { } index)
            await index.onDirectoryDeleted(absolutePath);
    }

    public async Task onFileRenamed(string oldAbsolutePath, string newAbsolutePath)
    {
        if (await GetActiveIndexAsync() is { } index)
            await index.onFileRenamed(oldAbsolutePath, newAbsolutePath);
    }

    public async Task onDirectoryRenamed(string oldAbsolutePath, string newAbsolutePath)
    {
        if (await GetActiveIndexAsync() is { } index)
            await index.onDirectoryRenamed(oldAbsolutePath, newAbsolutePath);
    }

    private static async Task DrainAsync(Task<Stream> fileData)
    {
        try
        {
            var stream = await fileData;
            await stream.DisposeAsync();
        }
        catch
        {
            // Nothing to release / already faulted — indexing was skipped anyway.
        }
    }

    // ══════════════════════════════════════════
    //  ISearchService — search
    // ══════════════════════════════════════════

    public async Task<List<FileDocument>> SearchAsync(
        string searchText, UserContext user,
        string? shareName = null, string? pathPrefix = null,
        CancellationToken ct = default, bool includeRecycleBin = false)
    {
        // Searching inside a recycle bin always shows its contents, whatever the toggle says.
        includeRecycleBin |= IsRecycleBinScope(pathPrefix);

        var engine = await GetActiveEngineAsync(ct);
        if (engine != SearchEngine.Filename)
        {
            try
            {
                var index = await GetIndexAsync(engine, ct);
                return await index.SearchAsync(searchText, user, shareName, pathPrefix, ct, includeRecycleBin);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                // The transport may wrap a cancellation in its own exception type. That is
                // the caller's timeout, not an index failure: no fallback, no warning.
                throw new OperationCanceledException(ct);
            }
            catch (Exception ex)
            {
                if (engine == SearchEngine.Local)
                    _logger.LogWarning(LogEvents.SearchLocalFailed, ex, LogMessages.SearchLocalFailed);
                else
                    _logger.LogWarning(LogEvents.SearchElasticFailed, ex, LogMessages.SearchElasticFailed);
                InvalidateState();
            }
        }

        return await _filename.SearchAsync(searchText, user, shareName, pathPrefix, ct, includeRecycleBin);
    }

    /// <summary>
    /// Whether a scope folder lies in a recycle bin. Any ".RECYCLE_BIN" segment counts: only the
    /// share's/home's own bin is indexed (dot folders elsewhere are excluded), so no depth is needed.
    /// </summary>
    internal static bool IsRecycleBinScope(string? pathPrefix)
        => (pathPrefix ?? string.Empty).Replace('\\', '/').Split('/')
            .Any(s => s.Equals(ShareEntryPolicy.RecycleBinName, StringComparison.OrdinalIgnoreCase));

    private async Task<ISearchService> GetIndexAsync(SearchEngine engine, CancellationToken ct)
    {
        if (engine == SearchEngine.Local)
            return _local;
        await EnsureEsInitializedAsync(ct);
        return _es;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var state = await ResolveStateAsync(forceFresh: true, ct);

        // The local index schema is created by migrations; only ES needs an index bootstrap.
        if (state.Engine == SearchEngine.Local)
            return;

        if (!state.Reachable)
        {
            _logger.LogDebug(LogEvents.SearchElasticUnreachable, LogMessages.SearchElasticUnreachable);
            return;
        }

        if (!state.Enabled)
        {
            _logger.LogDebug(LogEvents.SearchElasticDisabled, LogMessages.SearchElasticDisabled);
            return;
        }

        await EnsureEsInitializedAsync(ct);
    }

}
