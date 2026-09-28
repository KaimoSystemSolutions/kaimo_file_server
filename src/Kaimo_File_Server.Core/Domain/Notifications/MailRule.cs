using System.Text.Json;

namespace Kaimo_File_Server.Core.Domain.Notifications;

/// <summary>How a <see cref="RecipientSpec"/> value is resolved to mail addresses.</summary>
public enum RecipientKind
{
    /// <summary>A context role of the event, e.g. <c>affected</c> or <c>linkCreator</c>.</summary>
    ContextRole = 0,

    /// <summary>A user id.</summary>
    User = 1,

    /// <summary>A group id (all enabled members).</summary>
    Group = 2,

    /// <summary>A <c>ManagementPermission</c> name; all enabled users holding it globally.</summary>
    PermissionHolder = 3,

    /// <summary>A fixed external mail address.</summary>
    ExternalAddress = 4,
}

/// <summary>One recipient entry of a <see cref="MailRule"/>.</summary>
public sealed record RecipientSpec(RecipientKind Kind, string Value);

/// <summary>
/// "When event X happens, send its mail to these recipients." Several rules may exist per
/// event type (e.g. a welcome mail to the user plus a notice to administrators).
/// </summary>
public sealed class MailRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string EventType { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; }

    /// <summary>JSON array of <see cref="RecipientSpec"/>.</summary>
    public string RecipientsJson { get; set; } = "[]";

    /// <summary>Per dedup key and recipient, send at most one mail in this window (0 = no throttle).</summary>
    public int ThrottleMinutes { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? UpdatedByUserId { get; set; }

    public IReadOnlyList<RecipientSpec> GetRecipients()
    {
        try { return JsonSerializer.Deserialize<List<RecipientSpec>>(RecipientsJson) ?? []; }
        catch (JsonException) { return []; }
    }

    public void SetRecipients(IEnumerable<RecipientSpec> recipients)
        => RecipientsJson = JsonSerializer.Serialize(recipients.Distinct().ToList());
}
