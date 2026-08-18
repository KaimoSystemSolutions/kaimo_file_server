using Kaimo_File_Server.Core.Domain;

namespace Kaimo_File_Server.Core.Repositories;

/// <summary>Configuration and narrow runtime persistence for cloud syncs.</summary>
public interface ISyncDefinitionRepository
{
    Task<SyncDefinition?> GetBySharePathAsync(
        Guid shareId,
        string localPath,
        CancellationToken cancellationToken = default);

    Task<List<SyncDefinitionScheduleEntry>> GetEnabledScheduledAsync(
        CancellationToken cancellationToken = default);

    Task MarkCompletedAsync(
        Guid syncDefinitionId,
        DateTime completedAtUtc,
        CancellationToken cancellationToken = default);

    Task MarkFailedAsync(
        Guid syncDefinitionId,
        DateTime failedAtUtc,
        string errorCode,
        CancellationToken cancellationToken = default);
}

/// <summary>Scheduler projection containing configuration and its last success.</summary>
public sealed record SyncDefinitionScheduleEntry(
    SyncDefinition Definition,
    DateTime? LastSuccessfulRunAtUtc);

/// <summary>
/// Restart-safe compatibility importer used while the legacy editor still
/// writes mappings into a share's CloudSettings JSON.
/// </summary>
public interface ILegacyCloudSyncMigrationService
{
    Task EnsureMigratedAsync(CancellationToken cancellationToken = default);
}
