namespace Kaimo_File_Server.Core.Repositories.ExternalStorage;

/// <summary>Counts durable consumers that currently reference a connection.</summary>
public sealed record StorageConnectionUsage(int SyncCount, int VirtualShareCount, int BackupRepositoryCount = 0)
{
    public int TotalCount => checked(SyncCount + VirtualShareCount + BackupRepositoryCount);
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
