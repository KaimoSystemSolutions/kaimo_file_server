namespace Kaimo_File_Server.Core.Services.CloudSync;

/// <summary>
/// Restart-safe compatibility importer used while the legacy editor still
/// writes mappings into a share's CloudSettings JSON.
/// </summary>
public interface ILegacyCloudSyncMigrationService
{
    Task EnsureMigratedAsync(CancellationToken cancellationToken = default);
}
