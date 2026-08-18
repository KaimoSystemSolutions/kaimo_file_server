namespace Kaimo_File_Server.Core.Services;

/// <summary>Represents exclusive, renewable ownership of one connection's credential mutation path.</summary>
public interface IStorageConnectionCredentialLease : IAsyncDisposable;

/// <summary>
/// Coordinates refresh-token rotation and credential rewrap operations across
/// processes. A null result means another instance currently owns the lease.
/// </summary>
public interface IStorageConnectionCredentialLeaseManager
{
    Task<IStorageConnectionCredentialLease?> TryAcquireAsync(
        Guid connectionId,
        CancellationToken cancellationToken = default);
}
