namespace Kaimo_File_Server.Infrastructure.Notifications;

/// <summary>A fully rendered mail ready for the SMTP transport.</summary>
/// <param name="InlineLogo">Logo bytes referenced from the HTML as <c>cid:</c><see cref="MailTemplateRenderer.LogoContentId"/>.</param>
public sealed record MailMessageModel(
    string ToAddress,
    string Subject,
    string HtmlBody,
    string TextBody,
    byte[]? InlineLogo = null,
    string? InlineLogoContentType = null);

/// <summary>Outcome of <see cref="ISmtpMailSender.TestAsync"/>; <paramref name="Error"/> is user-readable.</summary>
public sealed record SmtpTestResult(bool Success, string? Error);

/// <summary>Thrown by <see cref="ISmtpMailSender.SendAsync"/> with a user-readable reason.</summary>
public sealed class MailSendException(string message, bool permanent, Exception? inner = null, bool transport = false)
    : Exception(message, inner)
{
    /// <summary>True when retrying cannot help (e.g. the recipient address was rejected).</summary>
    public bool Permanent { get; } = permanent;

    /// <summary>
    /// True when the SMTP server itself is unusable (unreachable, TLS, sign-in, not configured),
    /// so every mail would fail the same way. The dispatcher then pauses instead of spending
    /// the retry budget of each mail.
    /// </summary>
    public bool Transport { get; } = transport;
}

/// <summary>
/// SMTP transport. Implemented in the Web process, the only one that can decrypt the stored
/// SMTP password.
/// </summary>
public interface ISmtpMailSender
{
    /// <summary>Sends with the stored settings. Throws <see cref="MailSendException"/> on failure.</summary>
    Task SendAsync(MailMessageModel message, CancellationToken ct = default);

    /// <summary>
    /// Sends <paramref name="message"/> with unsaved <paramref name="draft"/> settings. When
    /// <paramref name="plainPassword"/> is empty the stored password is used. Never throws.
    /// </summary>
    Task<SmtpTestResult> TestAsync(SmtpSettings draft, string? plainPassword, MailMessageModel message, CancellationToken ct = default);
}
