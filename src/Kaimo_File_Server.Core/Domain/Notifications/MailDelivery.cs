namespace Kaimo_File_Server.Core.Domain.Notifications;

/// <summary>Send state of a <see cref="MailDelivery"/>.</summary>
public enum MailDeliveryStatus
{
    /// <summary>Rendered and waiting to be sent (also while SMTP is not configured).</summary>
    Pending = 0,

    /// <summary>Currently handed to the SMTP server.</summary>
    Sending = 1,

    Sent = 2,

    /// <summary>The last attempt failed; retried at <see cref="MailDelivery.NextAttemptUtc"/>.</summary>
    Failed = 3,

    /// <summary>All attempts failed; only a manual retry sends it again.</summary>
    Dead = 4,
}

/// <summary>
/// One rendered mail for one recipient of one event. Subject and bodies are rendered once
/// and stored, so a retry sends exactly what was rendered and the log shows what went out.
/// </summary>
public sealed class MailDelivery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary><see cref="NotificationEvent.Seq"/> of the source event.</summary>
    public long EventSeq { get; set; }
    public Guid RuleId { get; set; }

    /// <summary>Catalog key of the source event (denormalized for the log and throttling).</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>Copy of the event's dedup key, used for throttling.</summary>
    public string? DedupKey { get; set; }

    public string ToAddress { get; set; } = string.Empty;
    public Guid? ToUserId { get; set; }
    public string Language { get; set; } = "de";

    public string Subject { get; set; } = string.Empty;
    public string HtmlBody { get; set; } = string.Empty;
    public string TextBody { get; set; } = string.Empty;

    public MailDeliveryStatus Status { get; set; } = MailDeliveryStatus.Pending;
    public int AttemptCount { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime NextAttemptUtc { get; set; } = DateTime.UtcNow;
    public string? LastError { get; set; }
    public DateTime? SentAtUtc { get; set; }
}
