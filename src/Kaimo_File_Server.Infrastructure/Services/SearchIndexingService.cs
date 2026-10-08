using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Storage;
using Kaimo_File_Server.Infrastructure.Storage;
using Kaimo_File_Server.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Services;

/// <summary>
/// Owns ALL search index writes (Elasticsearch or the local PostgreSQL index, whichever engine is
/// selected) by tailing the durable <c>file_change_log</c> from a persisted per-engine cursor. The file-operation path no longer touches Elasticsearch (see
/// <see cref="FileServiceFactory"/>), so no upload, delete, rename or SMB close ever waits on
/// ES reachability or indexing — the change log is the buffer between them.
///
/// Robustness this buys over inline indexing:
///  • While ES is disabled or unreachable the cursor is NOT advanced, so the log accumulates and
///    the indexer catches up once ES returns (bounded only by the log's retention window).
///  • A single global, Seq-ordered scan gives a total order, so create/delete of the same path
///    can never race the way independent per-request indexers did.
///  • The index is derived and rebuildable: a lost log row or a poison entry is healed by the
///    manual full reindex, so one bad entry never blocks the pipeline.
///
/// Single-owner: exactly one process must run this service (registered only in the web host) so
/// the shared cursor is advanced by one writer.
/// ponytail: single-owner via single registration; add a DB advisory lock only if &gt;1 web replica.
/// </summary>
public sealed class SearchIndexingService(
    IServiceScopeFactory scopeFactory,
    IServiceProvider rootProvider,
    ISearchService search,
    ISearchAdminService searchAdmin,
    ISearchConfigStore configStore,
    ILogger<SearchIndexingService> logger) : BackgroundService
{
    private const int BatchSize = 200;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan EsInactiveInterval = TimeSpan.FromSeconds(10);

    // True once this process knows the local index is built or has started its build. Reset on
    // every engine change, so returning to Local re-checks whether a canceled build must restart.
    private bool _localBuildChecked;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Each index engine keeps its own cursor, so switching engines never makes one index
        // skip the changes it missed while the other was active.
        SearchEngine? cursorEngine = null;
        long cursor = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = PollInterval;
            try
            {
                // Only index against a live, selected index engine. Otherwise wait WITHOUT
                // advancing the cursor so the log keeps buffering until it returns.
                var state = await searchAdmin.GetStateAsync(stoppingToken);
                if (!state.Effective)
                {
                    await Sleep(EsInactiveInterval, stoppingToken);
                    continue;
                }

                if (cursorEngine != state.Engine)
                {
                    cursor = await LoadCursorAsync(state.Engine);
                    cursorEngine = state.Engine;
                    _localBuildChecked = false;
                }

                using var scope = scopeFactory.CreateScope();
                var sp = scope.ServiceProvider;
                var log = sp.GetRequiredService<IFileChangeLogRepository>();

                if (await ResolveCursorAsync(log, state.Engine, cursor, stoppingToken) is not { } resolved)
                {
                    // A required full build (first local build or change-log gap) could not start
                    // yet (e.g. a reindex of the previous engine is still winding down). Keep the
                    // cursor where it is and try again shortly.
                    await Sleep(EsInactiveInterval, stoppingToken);
                    continue;
                }
                cursor = resolved;

                var batch = await log.GetChangesSinceGlobalAsync(cursor, BatchSize, stoppingToken);
                if (batch.Count == 0)
                {
                    // Nothing new. `continue` would skip the poll delay at the loop end and
                    // spin against the database, so wait here explicitly.
                    await Sleep(PollInterval, stoppingToken);
                    continue;
                }

                var shares = await BuildShareMapAsync(sp, stoppingToken);
                foreach (var entry in batch)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    try
                    {
                        await IndexEntryAsync(entry, search, searchAdmin, shares);
                    }
                    catch (Exception ex)
                    {
                        // Skip the entry but keep going: the index is rebuildable and one bad
                        // row must never stall the pipeline behind it.
                        logger.LogWarning(
                            ex, "Search indexer failed on {ChangeType} {Path} (Seq {Seq}); skipping.",
                            entry.ChangeType, entry.Path, entry.Seq);
                    }
                    cursor = entry.Seq;
                }

                // ponytail: an engine switch during this batch lets the router send its tail to the
                // new engine while the old engine's cursor still advances; at most one batch is
                // missed there, healed by the gap check or a manual reindex.
                await configStore.SetIndexCursorAsync(state.Engine, cursor);

                // A full batch likely means more is waiting — loop immediately.
                if (batch.Count == BatchSize)
                    delay = TimeSpan.Zero;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Search indexer iteration failed.");
            }

            if (delay > TimeSpan.Zero)
                await Sleep(delay, stoppingToken);
        }
    }

    private async Task<long> LoadCursorAsync(SearchEngine engine)
    {
        try { return await configStore.GetIndexCursorAsync(engine); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Search indexer could not read its {Engine} cursor; starting from 0.", engine);
            return 0;
        }
    }

    /// <summary>
    /// Starts the full build of the local index until one has completed, fast-forwards the cursor
    /// on first start and repairs a gap: if the log prefix the cursor pointed after was pruned,
    /// the missed entries are gone, so a full reindex is triggered and the cursor jumps to the
    /// head. Returns null when a required full build could not be started yet; the cursor then
    /// stays put.
    /// </summary>
    private async Task<long?> ResolveCursorAsync(
        IFileChangeLogRepository log, SearchEngine engine, long cursor, CancellationToken ct)
    {
        long head = await log.GetHeadSeqGlobalAsync(ct);
        bool buildStarted = false;

        if (engine == SearchEngine.Local && !_localBuildChecked)
        {
            // An empty or partially built local index (first enable, or a build interrupted by a
            // restart or an engine switch) is built from disk; the log is followed meanwhile.
            // Checked once per engine activation, so a running build is not restarted per poll.
            if (!await configStore.GetLocalIndexBuiltAsync())
            {
                if (!await searchAdmin.TryStartReindexAsync(shareName: null, ct))
                    return null;
                buildStarted = true;
            }
            _localBuildChecked = true;
        }

        if (cursor == 0)
        {
            // Bootstrap: start from now. For Elasticsearch, pre-existing files were indexed by the
            // old write path (or are covered by a manual reindex); the local index is covered by
            // its full build above. Replaying the whole log would be wasteful.
            if (head > 0)
                await configStore.SetIndexCursorAsync(engine, head);
            return head;
        }

        long oldest = await log.GetOldestSeqAsync(ct);
        if (oldest > cursor + 1)
        {
            // Entries between our cursor and the oldest retained row were pruned → gap.
            logger.LogWarning(
                "Search indexer detected a change-log gap (cursor {Cursor} < oldest {Oldest}); " +
                "triggering a full reindex.", cursor, oldest);
            // Jumping to the head without a running rebuild would lose the gap for good; a local
            // build started just above already covers it.
            if (!buildStarted && !await searchAdmin.TryStartReindexAsync(shareName: null, ct))
                return null;
            if (head > 0)
                await configStore.SetIndexCursorAsync(engine, head);
            return head;
        }

        return cursor;
    }

    private async Task<IReadOnlyDictionary<Guid, ShareTarget>> BuildShareMapAsync(
        IServiceProvider sp, CancellationToken ct)
    {
        var shareRepo = sp.GetRequiredService<IShareRepository>();
        var shares = await shareRepo.GetAllAsync();
        var map = new Dictionary<Guid, ShareTarget>(shares.Count);
        foreach (var share in shares)
        {
            // Same storage construction as FileServiceFactory.
            var storage = new FileSystemStorage(share.Path, share.Id, rootProvider);
            map[share.Id] = new ShareTarget(storage, share.Name, share.RecycleRootDepth);
        }
        return map;
    }

    /// <summary>
    /// Maps one change-log entry onto the search backend. Static and dependency-injected so it can
    /// be unit-tested without the hosting loop.
    /// </summary>
    internal static async Task IndexEntryAsync(
        FileChangeLogEntry entry,
        ISearchService search,
        ISearchAdminService searchAdmin,
        IReadOnlyDictionary<Guid, ShareTarget> shares)
    {
        if (!shares.TryGetValue(entry.ShareId, out var target))
            return; // share removed/disabled — nothing to index

        var storage = target.Storage;

        // Hidden paths (".git", ".versions", …) are never indexed, exactly like the full reindex
        // skips them (ShareEntryPolicy.IsExcludedFromSearch; the recycle bin is indexed). Deletes
        // are still applied, so nothing indexed earlier can linger there.
        switch (entry.ChangeType)
        {
            case FileChangeType.Created:
            case FileChangeType.Modified:
                if (IsHiddenPath(entry.Path, target.RecycleRootDepth))
                    break;
                if (entry.IsDirectory)
                    await search.onDirectoryCreated(storage.ToAbsolutePath(entry.Path));
                else
                    await search.onFileCreated(
                        storage.ToAbsolutePath(entry.Path), OpenForIndexing(storage, entry));
                break;

            case FileChangeType.Deleted:
                var absDeleted = storage.ToAbsolutePath(entry.Path);
                if (entry.IsDirectory)
                    await search.onDirectoryDeleted(absDeleted);
                else
                    await search.onFileDeleted(absDeleted);
                break;

            case FileChangeType.Renamed:
                var newAbs = storage.ToAbsolutePath(entry.Path);
                var oldAbs = storage.ToAbsolutePath(entry.OldPath ?? entry.Path);
                bool newHidden = IsHiddenPath(entry.Path, target.RecycleRootDepth);
                bool oldHidden = IsHiddenPath(entry.OldPath ?? entry.Path, target.RecycleRootDepth);

                if (newHidden)
                {
                    // Moved into a hidden folder: the entry leaves the index instead of
                    // following the file there. (Deletes to the recycle bin are plain renames.)
                    if (oldHidden)
                        break;
                    if (entry.IsDirectory)
                        await search.onDirectoryDeleted(oldAbs);
                    else
                        await search.onFileDeleted(oldAbs);
                    break;
                }

                if (oldHidden)
                {
                    // Moved out of a hidden folder: the index has nothing to move, so index the
                    // target fresh. A folder brings an unbounded subtree — reindex its share,
                    // like SubtreeChanged.
                    if (entry.IsDirectory)
                        await searchAdmin.TryStartReindexAsync(target.ShareName);
                    else
                        await search.onFileCreated(newAbs, OpenForIndexing(storage, entry));
                    break;
                }

                if (entry.IsDirectory)
                    await search.onDirectoryRenamed(oldAbs, newAbs);
                else
                    await search.onFileRenamed(oldAbs, newAbs);
                break;

            case FileChangeType.SubtreeChanged:
                // Bulk change of an unbounded subtree — reindex the whole share (idempotent,
                // path-derived doc ids). ponytail: coarse; a scoped subtree walk can replace this.
                await searchAdmin.TryStartReindexAsync(target.ShareName);
                break;
        }
    }

    /// <summary>
    /// SMB creates files via open: <c>Created</c> is logged while the writer still holds the
    /// file exclusively, and its content is logged as <c>Modified</c> when the handle closes.
    /// A locked <c>Created</c> file is therefore indexed by name only (empty content); the
    /// later <c>Modified</c> entry upserts the same document with the real content.
    /// </summary>
    private static Task<Stream> OpenForIndexing(IStorageEngine storage, FileChangeLogEntry entry)
    {
        try
        {
            return storage.ReadAsync(entry.Path, preserveAccessTime: true);
        }
        catch (IOException ex) when (entry.ChangeType == FileChangeType.Created
                                     && ex is not FileNotFoundException
                                     && ex is not DirectoryNotFoundException)
        {
            return Task.FromResult(Stream.Null);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Deleted/renamed before we got here: let the search service treat it as vanished.
            return Task.FromException<Stream>(ex);
        }
    }

    /// <summary>The reindex rule: dot segments are hidden, the share's/home's recycle bin is not.</summary>
    private static bool IsHiddenPath(string sharePath, int recycleRootDepth)
        => ShareEntryPolicy.IsExcludedFromSearch(sharePath, recycleRootDepth);

    private static async Task Sleep(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    /// <summary>A share resolved to its storage engine, advertised name and recycle-bin depth.</summary>
    internal readonly record struct ShareTarget(IStorageEngine Storage, string ShareName, int RecycleRootDepth = 0);
}
