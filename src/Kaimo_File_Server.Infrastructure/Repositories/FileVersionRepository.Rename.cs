using System.Data;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Repositories
{
    public partial class FileVersionRepository
    {
        // Runs under the execution strategy, so a serialization failure (40001) or a
        // dropped connection replays the whole unit on a fresh context.
        public Task<List<FileVersion>> RenamePathAsync(
            Guid shareId,
            string oldPath,
            string newPath,
            Guid? sambaLifecycleEventId = null)
        {
            var attempt = new RenameAttempt();
            return _dbFactory.ExecuteResilientAsync(
                db => RenamePathCoreAsync(db, shareId, oldPath, newPath, sambaLifecycleEventId, attempt));
        }

        /// <summary>The plan the first attempt of a rename without event receipt made before it wrote.</summary>
        private sealed class RenameAttempt
        {
            public VersionRenamePlan? Plan;
        }

        private static async Task<List<FileVersion>> RenamePathCoreAsync(
            ApplicationDbContext db,
            Guid shareId,
            string oldPath,
            string newPath,
            Guid? sambaLifecycleEventId,
            RenameAttempt attempt)
        {
            // One transaction, so a failure between removing the displaced rows and moving the
            // source never leaves half a rename behind. Serializable isolation also closes the
            // rare overlap where an expired event lease is reclaimed while the prior worker commits.
            await using var transaction = sambaLifecycleEventId.HasValue
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable)
                : await db.Database.BeginTransactionAsync();
            SambaLifecycleEventReceipt? receipt = null;
            if (sambaLifecycleEventId.HasValue)
            {
                // Row-lock the receipt as the very first statement. The bridge renews the
                // event lease on this row every 30 s; without the lock a renewal committing
                // mid-transaction fails the checkpoint write below with a serialization
                // error (40001), and a rename that always outlasts the renewal interval
                // would never get through. With the lock the renewal waits until commit, so
                // a conflict is only possible here, where a retry costs nothing.
                await db.SambaLifecycleEventReceipts
                    .Where(eventReceipt => eventReceipt.EventId == sambaLifecycleEventId.Value)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(eventReceipt => eventReceipt.LeaseUntilUtc,
                            eventReceipt => eventReceipt.LeaseUntilUtc));

                receipt = await db.SambaLifecycleEventReceipts.SingleOrDefaultAsync(
                    eventReceipt => eventReceipt.EventId == sambaLifecycleEventId.Value);
                if (receipt is null ||
                    !StringComparer.Ordinal.Equals(receipt.EventType, "rename"))
                {
                    throw new InvalidOperationException(
                        $"Samba rename event {sambaLifecycleEventId.Value:N} has no matching receipt.");
                }

                // The version rows and this checkpoint are committed together.
                // A retry after any later lifecycle effect failed must never
                // reinterpret the now-empty source as a fresh replace rename.
                if (receipt.RenameVersionsCompletedAtUtc.HasValue)
                {
                    await transaction.CommitAsync();
                    return [];
                }
            }

            VersionRenamePlan plan;
            if (receipt is null && attempt.Plan is not null)
            {
                // Replay without a checkpoint. It may follow a COMMIT whose acknowledgement was
                // lost (rows already changed) or a real rollback (nothing applied); the current
                // rows cannot tell the two apart. Re-applying the first attempt's plan by row id
                // is correct in both cases: every change is idempotent per id.
                plan = attempt.Plan;
                db.GetDatabaseLogger().LogWarning(
                    "Replaying version rename {OldPath} -> {NewPath} in share {ShareId} with the plan of its first " +
                    "attempt ({Moved} moved, {Displaced} displaced).",
                    oldPath, newPath, shareId, plan.NewPaths.Count, plan.Displaced.Count);
            }
            else
            {
                var source = await UnderPath(db, shareId, oldPath).ToListAsync();
                var sourceIds = source.Select(v => v.Id).ToHashSet();
                var destination = await UnderPath(db, shareId, newPath)
                    .Where(v => !sourceIds.Contains(v.Id))
                    .ToListAsync();
                plan = VersionHistoryMerge.Plan(oldPath, newPath, source, destination, DateTime.UtcNow);

                // Recorded before the first write, so it exists whenever a COMMIT may have happened.
                if (receipt is null) attempt.Plan = plan;
            }

            await ApplyRenamePlanAsync(db, plan);

            if (receipt is not null)
            {
                receipt.RenameVersionsCompletedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }

            await transaction.CommitAsync();
            return [.. plan.Displaced];
        }

        private static IQueryable<FileVersion> UnderPath(ApplicationDbContext db, Guid shareId, string path)
        {
            var query = db.Set<FileVersion>().Where(v => v.ShareId == shareId);
            if (path.Length == 0) return query;
            var prefix = path + "/";
            return query.Where(v => v.FilePath == path || v.FilePath.StartsWith(prefix));
        }

        private static async Task ApplyRenamePlanAsync(ApplicationDbContext db, VersionRenamePlan plan)
        {
            // Displaced rows go first: they hold the (path, second) slots the moved rows take.
            if (plan.Displaced.Count > 0)
            {
                var displacedIds = plan.Displaced.Select(v => v.Id).ToList();
                db.Set<FileVersion>().RemoveRange(
                    await db.Set<FileVersion>().Where(v => displacedIds.Contains(v.Id)).ToListAsync());
                await db.SaveChangesAsync();
            }

            var touchedIds = plan.NewPaths.Keys.Concat(plan.VersionNumbers.Keys).Distinct().ToList();
            var touched = touchedIds.Count == 0
                ? []
                : await db.Set<FileVersion>().Where(v => touchedIds.Contains(v.Id)).ToListAsync();
            foreach (var version in touched)
            {
                if (plan.NewPaths.TryGetValue(version.Id, out var path)) version.FilePath = path;
                if (plan.VersionNumbers.TryGetValue(version.Id, out var number)) version.VersionNumber = number;
            }

            if (plan.LiveCopies.Count > 0)
            {
                var copyIds = plan.LiveCopies.Select(v => v.Id).ToList();
                var present = await db.Set<FileVersion>().Where(v => copyIds.Contains(v.Id))
                    .Select(v => v.Id).ToListAsync();
                db.Set<FileVersion>().AddRange(plan.LiveCopies
                    .Where(v => !present.Contains(v.Id))
                    .Select(v => new FileVersion(v.ShareId, v.FilePath, v.SnapshotTimestampUtc, v.StoragePath,
                        v.ContentHash, v.Size, v.CreatedBy, v.VersionNumber) { Id = v.Id }));
            }

            await db.SaveChangesAsync();
        }
    }
}
