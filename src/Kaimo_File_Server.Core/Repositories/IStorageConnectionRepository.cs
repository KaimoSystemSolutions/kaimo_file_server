using Kaimo_File_Server.Core.Domain;

namespace Kaimo_File_Server.Core.Repositories;

/// <summary>Counts durable consumers that currently reference a connection.</summary>
public sealed record StorageConnectionUsage(int SyncCount, int VirtualShareCount)
{
    public int TotalCount => checked(SyncCount + VirtualShareCount);
}

public enum StorageConnectionDeleteResult
{
    Deleted,
    NotFound,
    InUse
}

/// <summary>Raised when a stale connection snapshot would overwrite a newer write.</summary>
public sealed class StorageConnectionConcurrencyException(Guid connectionId, Exception? innerException = null)
    : InvalidOperationException($"Storage connection '{connectionId:D}' was changed by another operation.", innerException)
{
    public Guid ConnectionId { get; } = connectionId;
}

/// <summary>Persistence boundary for reusable external-storage connections.</summary>
public interface IStorageConnectionRepository
{
    Task<List<StorageConnection>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<StorageConnection?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task SaveAsync(StorageConnection connection, CancellationToken cancellationToken = default);

    Task UpdateRuntimeAsync(
        Guid id,
        string encryptedCredentialPayload,
        string? accountDisplayName,
        string? accountEmail,
        StorageConnectionState state,
        string? lastErrorCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces only the encrypted credential when the caller still owns the
    /// observed concurrency version. This prevents a rewrap from overwriting a
    /// provider refresh that completed after the batch loaded its snapshot.
    /// </summary>
    Task<bool> TryUpdateCredentialAsync(
        Guid id,
        long expectedConcurrencyVersion,
        string encryptedCredentialPayload,
        int protectorPurposeVersion,
        CancellationToken cancellationToken = default);

    Task<StorageConnectionUsage> GetUsageAsync(Guid id, CancellationToken cancellationToken = default);
    Task<StorageConnectionDeleteResult> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
