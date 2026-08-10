using Kaimo_File_Server.Core.Domain;

namespace Kaimo_File_Server.Core.Repositories;

public interface ICloudAccessRepository
{
    Task<List<CloudAccessConnection>> GetConnectionsAsync(CancellationToken cancellationToken = default);
    Task<CloudAccessConnection?> GetConnectionAsync(Guid id, CancellationToken cancellationToken = default);
    Task UpsertConnectionAsync(CloudAccessConnection connection, CancellationToken cancellationToken = default);
    Task UpdateConnectionRuntimeAsync(
        Guid id,
        string protectedCredentials,
        string? accountDisplayName,
        string? accountEmail,
        CloudAccessConnectionState state,
        string? lastError,
        CancellationToken cancellationToken = default);
    Task DeleteConnectionAsync(Guid id, CancellationToken cancellationToken = default);

    Task<List<CloudAccessShare>> GetSharesAsync(CancellationToken cancellationToken = default);
    Task<CloudAccessShare?> GetShareAsync(Guid id, CancellationToken cancellationToken = default);
    Task UpsertShareAsync(CloudAccessShare share, CancellationToken cancellationToken = default);
    Task DeleteShareAsync(Guid id, CancellationToken cancellationToken = default);

    Task<HashSet<Guid>> GetPrincipalIdsAsync(Guid shareId, CancellationToken cancellationToken = default);
    Task ReplaceGrantsAsync(Guid shareId, IEnumerable<CloudAccessGrant> grants, CancellationToken cancellationToken = default);
    Task<bool> HasGrantAsync(Guid shareId, IEnumerable<Guid> principalIds, CancellationToken cancellationToken = default);
}
