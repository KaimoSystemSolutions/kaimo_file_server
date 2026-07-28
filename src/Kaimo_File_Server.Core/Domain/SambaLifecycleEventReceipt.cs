namespace Kaimo_File_Server.Core.Domain;

/// <summary>
/// Persistent idempotency receipt for one Samba lifecycle event. A receipt is
/// completed only after all current lifecycle effects returned successfully.
/// An expired processing lease may be reclaimed after a bridge crash.
/// </summary>
public sealed class SambaLifecycleEventReceipt
{
    public Guid EventId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LeaseUntilUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
}
