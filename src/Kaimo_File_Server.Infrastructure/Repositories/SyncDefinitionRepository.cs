using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Repositories;

/// <summary>EF Core persistence for first-class sync configuration and runtime state.</summary>
public sealed class SyncDefinitionRepository(IDbContextFactory<ApplicationDbContext> dbFactory)
    : ISyncDefinitionRepository
{
    public async Task<List<SyncDefinitionAdminEntry>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await (
                from sync in db.SyncDefinitions.AsNoTracking()
                join runtime in db.SyncDefinitionRuntimes.AsNoTracking()
                    on sync.Id equals runtime.SyncDefinitionId into runtimes
                from runtime in runtimes.DefaultIfEmpty()
                orderby sync.DisplayName, sync.LocalPath
                select new SyncDefinitionAdminEntry(sync, runtime))
            .ToListAsync(cancellationToken);
    }

    public async Task<SyncDefinition?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.SyncDefinitions.AsNoTracking()
            .SingleOrDefaultAsync(sync => sync.Id == id, cancellationToken);
    }

    public async Task<SyncDefinition?> GetBySharePathAsync(
        Guid shareId,
        string localPath,
        CancellationToken cancellationToken = default)
    {
        string normalizedPath = ShareRelativePath.Normalize(localPath);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.SyncDefinitions.AsNoTracking().SingleOrDefaultAsync(
            sync => sync.LocalShareId == shareId && sync.LocalPath == normalizedPath,
            cancellationToken);
    }

    public async Task<List<SyncDefinitionScheduleEntry>> GetEnabledScheduledAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entries = await (
                from sync in db.SyncDefinitions.AsNoTracking()
                join share in db.ShareDefinitions.AsNoTracking()
                    on sync.LocalShareId equals share.Id
                join runtime in db.SyncDefinitionRuntimes.AsNoTracking()
                    on sync.Id equals runtime.SyncDefinitionId into runtimes
                from runtime in runtimes.DefaultIfEmpty()
                where sync.Enabled && share.IsEnabled
                select new SyncDefinitionScheduleEntry(
                    sync,
                    runtime == null ? null : runtime.LastSuccessfulRunAtUtc))
            .ToListAsync(cancellationToken);
        return entries.Where(entry => entry.Definition.Schedule.IsEnabled).ToList();
    }

    public async Task SaveAsync(
        SyncDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.LocalPath = ShareRelativePath.Normalize(definition.LocalPath);
        definition.RemotePath = NormalizeRemotePath(definition.RemotePath);
        definition.DisplayName = definition.DisplayName.Trim();
        definition.Description = definition.Description.Trim();
        definition.UpdatedAtUtc = DateTime.UtcNow;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        bool exists = await db.SyncDefinitions.AsNoTracking()
            .AnyAsync(sync => sync.Id == definition.Id, cancellationToken);
        if (exists)
        {
            // Any edit through the first-class UI makes this row authoritative.
            // The source checksum is retained only for diagnostics.
            definition.MigrationSource = null;
            db.SyncDefinitions.Update(definition);
        }
        else
        {
            definition.CreatedAtUtc = DateTime.UtcNow;
            definition.MigrationSource = null;
            definition.MigrationSourceChecksum = null;
            db.SyncDefinitions.Add(definition);
            db.SyncDefinitionRuntimes.Add(new SyncDefinitionRuntime
            {
                SyncDefinitionId = definition.Id,
                UpdatedAtUtc = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var definition = await db.SyncDefinitions.SingleOrDefaultAsync(
            sync => sync.Id == id, cancellationToken);
        if (definition is null)
            return false;

        if (definition.MigrationSource == SyncDefinition.LegacyCloudSettingsSource)
        {
            definition.Enabled = false;
            definition.MigrationSource = SyncDefinition.DeletedByFirstClassEditorSource;
            definition.UpdatedAtUtc = DateTime.UtcNow;
        }
        else
        {
            db.SyncDefinitions.Remove(definition);
        }

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task MarkCompletedAsync(
        Guid syncDefinitionId,
        DateTime completedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var runtime = await db.SyncDefinitionRuntimes.SingleOrDefaultAsync(
            item => item.SyncDefinitionId == syncDefinitionId,
            cancellationToken);
        if (runtime is null)
        {
            runtime = new SyncDefinitionRuntime { SyncDefinitionId = syncDefinitionId };
            db.SyncDefinitionRuntimes.Add(runtime);
        }

        runtime.LastRunAtUtc = completedAtUtc;
        runtime.LastSuccessfulRunAtUtc = completedAtUtc;
        runtime.CurrentJobId = null;
        runtime.LeaseOwner = null;
        runtime.ProgressPercent = 100;
        runtime.LastErrorCode = null;
        runtime.UpdatedAtUtc = completedAtUtc;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkFailedAsync(
        Guid syncDefinitionId,
        DateTime failedAtUtc,
        string errorCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var runtime = await db.SyncDefinitionRuntimes.SingleOrDefaultAsync(
            item => item.SyncDefinitionId == syncDefinitionId,
            cancellationToken);
        if (runtime is null)
        {
            runtime = new SyncDefinitionRuntime { SyncDefinitionId = syncDefinitionId };
            db.SyncDefinitionRuntimes.Add(runtime);
        }

        runtime.LastRunAtUtc = failedAtUtc;
        runtime.CurrentJobId = null;
        runtime.LeaseOwner = null;
        runtime.ProgressPercent = null;
        runtime.LastErrorCode = errorCode[..Math.Min(errorCode.Length, 200)];
        runtime.UpdatedAtUtc = failedAtUtc;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string NormalizeRemotePath(string? path)
    {
        string normalized = string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Replace('\\', '/').Trim('/');
        return normalized.Length == 0 ? "/" : $"/{normalized}";
    }
}
