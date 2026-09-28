namespace Kaimo_File_Server.Core.Domain.Notifications;

/// <summary>Processing state of an outbox <see cref="NotificationEvent"/>.</summary>
public enum NotificationEventStatus
{
    /// <summary>Waiting for the dispatcher.</summary>
    Pending = 0,

    /// <summary>Claimed by the dispatcher (see <see cref="NotificationEvent.LeaseUntilUtc"/>).</summary>
    Processing = 1,

    /// <summary>Mail deliveries were created.</summary>
    Done = 2,

    /// <summary>No enabled rule or no recipient applied; nothing was sent.</summary>
    Skipped = 3,

    /// <summary>Processing failed repeatedly and was given up.</summary>
    Failed = 4,
}

/// <summary>
/// Outbox row for a business event that may trigger mail notifications. Any process
/// (Web, Host, SmbBridge) inserts these through <c>INotificationPublisher</c>; only the
/// Web process dispatches them. The payload holds placeholder values for the templates
/// and must never contain secrets (passwords, tokens, link secrets).
/// </summary>
public sealed class NotificationEvent
{
    /// <summary>Monotonic database identity (primary key); gives the dispatch order.</summary>
    public long Seq { get; set; }

    /// <summary>Catalog key, e.g. <c>sharelink.upload_received</c>.</summary>
    public string Type { get; set; } = string.Empty;

    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>The user who triggered the event, when there is one.</summary>
    public Guid? ActorUserId { get; set; }

    /// <summary>JSON object: context role → user id, e.g. <c>{"affected":"…","linkCreator":"…"}</c>.</summary>
    public string SubjectUserIdsJson { get; set; } = "{}";

    /// <summary>JSON object: placeholder name → string value.</summary>
    public string PayloadJson { get; set; } = "{}";

    /// <summary>Optional key used by rule throttling (same key + recipient within the window is sent once).</summary>
    public string? DedupKey { get; set; }

    public NotificationEventStatus Status { get; set; } = NotificationEventStatus.Pending;
    public DateTime? LeaseUntilUtc { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public DateTime? ProcessedAtUtc { get; set; }
}
