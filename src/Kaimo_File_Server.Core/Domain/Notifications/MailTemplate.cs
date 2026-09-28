namespace Kaimo_File_Server.Core.Domain.Notifications;

/// <summary>
/// An administrator's customization of the built-in mail template of one event type in one
/// language. When no row exists the built-in default is used, so defaults can improve with
/// updates; "reset to default" simply deletes the row.
/// </summary>
public sealed class MailTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string EventType { get; set; } = string.Empty;

    /// <summary>Two-letter language code (<c>de</c> or <c>en</c>).</summary>
    public string Language { get; set; } = "de";

    /// <summary>Liquid source of the subject line (rendered as plain text).</summary>
    public string SubjectTemplate { get; set; } = string.Empty;

    /// <summary>Liquid source of the HTML body (wrapped into the shared layout).</summary>
    public string HtmlTemplate { get; set; } = string.Empty;

    /// <summary>Optional Liquid source of the plain-text part; generated from the HTML when empty.</summary>
    public string? TextTemplate { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? UpdatedByUserId { get; set; }
}
