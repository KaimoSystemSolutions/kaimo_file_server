using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Storage;
using Kaimo_File_Server.Infrastructure.Storage;
using Kaimo_File_Server.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Services;

/// <summary>
/// Owns ALL Elasticsearch index writes by tailing the durable <c>file_change_log</c> from a
/// persisted cursor. The file-operation path no longer touches Elasticsearch (see
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        long cursor;
        try { cursor = await configStore.GetIndexCursorAsync(); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Search indexer could not read its cursor; starting from 0.");
            cursor = 0;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = PollInterval;
            try
            {
                // Only index against a live, enabled Elasticsearch. Otherwise wait WITHOUT
                // advancing the cursor so the log keeps buffering until ES returns.
                if (!(await searchAdmin.GetStateAsync(stoppingToken)).Effective)
                {
                    await Sleep(EsInactiveInterval, stoppingToken);
                    continue;
                }

                using var scope = scopeFactory.CreateScope();
                var sp = scope.ServiceProvider;
                var log = sp.GetRequiredService<IFileChangeLogRepository>();

                cursor = await ResolveCursorAsync(log, cursor, stoppingToken);

                var batch = await log.GetChangesSinceGlobalAsync(cursor, BatchSize, stoppingToken);
                if (batch.Count == 0)
                    continue; // nothing new — fall through to the poll delay

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

                await configStore.SetIndexCursorAsync(cursor);

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

    /// <summary>
    /// Fast-forwards the cursor on first start (existing content is already indexed) and repairs a
    /// gap: if the log prefix the cursor pointed after was pruned, the missed entries are gone, so
    /// a full reindex is triggered and the cursor jumps to the head.
    /// </summary>
    private async Task<long> ResolveCursorAsync(
        IFileChangeLogRepository log, long cursor, CancellationToken ct)
    {
        long head = await log.GetHeadSeqGlobalAsync(ct);

        if (cursor == 0)
        {
            // Bootstrap: start from now. Pre-existing files were indexed by the old write path
            // (or are covered by a manual reindex); replaying the whole log would be wasteful.
            if (head > 0)
                await configStore.SetIndexCursorAsync(head);
            return head;
        }

        long oldest = await log.GetOldestSeqAsync(ct);
        if (oldest > cursor + 1)
        {
            // Entries between our cursor and the oldest retained row were pruned → gap.
            logger.LogWarning(
                "Search indexer detected a change-log gap (cursor {Cursor} < oldest {Oldest}); " +
                "triggering a full reindex.", cursor, oldest);
            await searchAdmin.TryStartReindexAsync(shareName: null, ct);
            if (head > 0)
                await configStore.SetIndexCursorAsync(head);
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
            map[share.Id] = new ShareTarget(storage, share.Name);
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

        switch (entry.ChangeType)
        {
            case FileChangeType.Created:
            case FileChangeType.Modified:
                if (entry.IsDirectory)
                    await search.onDirectoryCreated(storage.ToAbsolutePath(entry.Path));
                else
                    await search.onFileCreated(
                        storage.ToAbsolutePath(entry.Path), storage.ReadAsync(entry.Path));
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

    private static async Task Sleep(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    /// <summary>A share resolved to its storage engine and advertised name.</summary>
    internal readonly record struct ShareTarget(IStorageEngine Storage, string ShareName);
}
