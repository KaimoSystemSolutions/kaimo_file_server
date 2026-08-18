namespace Kaimo_File_Server.Core.Domain;

/// <summary>
/// Short-lived, single-use proof for an external-storage authorization flow.
/// Only a SHA-256 hash of the browser-visible token is persisted.
/// </summary>
public sealed class StorageAuthorizationTransaction
{
    public string TokenHash { get; set; } = string.Empty;
    public Guid ResourceId { get; set; }
    public string ResourcePath { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public Guid? InitiatingUserId { get; set; }
    public Guid? DepartmentId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? ConsumedAtUtc { get; set; }
}

/// <summary>
/// Durable coordination row that serializes credential refresh and rewrap work
/// for one connection across all application instances.
/// </summary>
public sealed class StorageConnectionCredentialLease
{
    public Guid ConnectionId { get; set; }
    public Guid LeaseId { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
}

/// <summary>
/// Shared Microsoft device-authorization polling state. The device code and
/// local hand-off context are stored only inside <see cref="ProtectedPayload"/>.
/// </summary>
public sealed class StorageDeviceAuthorizationSession
{
    public string SessionHash { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public string ProtectedPayload { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public int PollIntervalSeconds { get; set; }
    public DateTime NextPollAtUtc { get; set; }
    public Guid? PollLeaseId { get; set; }
    public DateTime? PollLeaseUntilUtc { get; set; }
}
