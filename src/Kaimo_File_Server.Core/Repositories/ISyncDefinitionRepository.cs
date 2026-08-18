using Kaimo_File_Server.Core.Domain;

namespace Kaimo_File_Server.Core.Repositories;

/// <summary>Configuration and narrow runtime persistence for cloud syncs.</summary>
public interface ISyncDefinitionRepository
{
    /// <summary>Returns every durable sync together with its optional runtime state.</summary>
    Task<List<SyncDefinitionAdminEntry>> GetAllAsync(
        CancellationToken cancellationToken = default);

    Task<SyncDefinition?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<SyncDefinition?> GetBySharePathAsync(
        Guid shareId,
        string localPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates a first-class sync configuration. Provider credentials
    /// and mutable run state are never accepted by this operation.
    /// </summary>
    Task SaveAsync(
        SyncDefinition definition,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a native first-class definition. Imported definitions are retained
    /// as disabled cutover tombstones so the compatibility importer cannot recreate them.
    /// </summary>
    Task<bool> DeleteAsync(
        Guid id,
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

/// <summary>Administration projection that keeps configuration and run state separate.</summary>
public sealed record SyncDefinitionAdminEntry(
    SyncDefinition Definition,
    SyncDefinitionRuntime? Runtime);

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
