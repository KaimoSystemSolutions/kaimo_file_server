using System.Net.Sockets;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Notifications;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Kaimo_File_Server.Web.Services.Notifications;

/// <summary>
/// SMTP transport based on MailKit. Opens one connection per mail (the dispatcher sends a
/// few mails per minute at most). Server certificates are always validated; failures are
/// mapped to user-readable, localized messages. The password is never logged.
/// </summary>
public sealed class MailKitSmtpMailSender(
    ISmtpConfigStore store,
    ICredentialVault vault,
    ILogger<MailKitSmtpMailSender> logger) : ISmtpMailSender
{
    public async Task SendAsync(MailMessageModel message, CancellationToken ct = default)
    {
        var settings = await store.GetAsync();
        if (!settings.IsUsable)
            throw new MailSendException(R("Web_MailServer_Error_NotConfigured"), permanent: false, transport: true);
        await SendCoreAsync(settings, StoredPassword(settings), message, ct);
    }

    public async Task<SmtpTestResult> TestAsync(
        SmtpSettings draft, string? plainPassword, MailMessageModel message, CancellationToken ct = default)
    {
        try
        {
            var password = plainPassword;
            if (string.IsNullOrEmpty(password) && draft.AuthMode == SmtpAuthMode.Password)
            {
                var stored = await store.GetAsync();
                if (!string.IsNullOrEmpty(stored.EncryptedPassword) && !draft.CanReuseStoredPasswordOf(stored))
                    return new SmtpTestResult(false, R("Web_MailServer_Error_PasswordRequired"));
                password = StoredPassword(stored);
            }
            await SendCoreAsync(draft, password, message, ct);
            return new SmtpTestResult(true, null);
        }
        catch (MailSendException ex)
        {
            return new SmtpTestResult(false, ex.Message);
        }
    }

    private string? StoredPassword(SmtpSettings settings)
    {
        if (settings.AuthMode != SmtpAuthMode.Password || string.IsNullOrEmpty(settings.EncryptedPassword))
            return null;
        try
        {
            return vault.Unprotect<string>(settings.EncryptedPassword, SmtpSettings.PasswordContext);
        }
        catch (Exception ex)
        {
            // E.g. the Data Protection key ring was replaced; the admin must re-enter it.
            logger.LogWarning(ex, "The stored SMTP password could not be decrypted");
            throw new MailSendException(R("Web_MailServer_Error_PasswordUnreadable"), permanent: false, ex, transport: true);
        }
    }

    private async Task SendCoreAsync(SmtpSettings settings, string? password, MailMessageModel model, CancellationToken ct)
    {
        settings = settings with { };
        settings.Normalize();
        if (string.IsNullOrWhiteSpace(settings.Host) || string.IsNullOrWhiteSpace(settings.FromAddress))
            throw new MailSendException(R("Web_MailServer_Error_NotConfigured"), permanent: false, transport: true);

        var message = BuildMessage(settings, model);

        using var client = new SmtpClient { Timeout = settings.TimeoutSeconds * 1000 };
        try
        {
            await client.ConnectAsync(settings.Host, settings.Port, MapSecurity(settings.Security), ct);
            if (settings.AuthMode == SmtpAuthMode.Password && settings.Username.Length > 0)
                await client.AuthenticateAsync(settings.Username, password ?? string.Empty, ct);
            await client.SendAsync(message, ct);
            await client.DisconnectAsync(quit: true, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogInformation("SMTP send to {Host}:{Port} failed: {Error}", settings.Host, settings.Port, ex.Message);
            throw Map(ex, settings);
        }
    }

    private static MimeMessage BuildMessage(SmtpSettings settings, MailMessageModel model)
    {
        var message = new MimeMessage();
        if (!MailboxAddress.TryParse(settings.FromAddress, out var from))
            throw new MailSendException(R("Web_MailServer_Error_Sender"), permanent: false, transport: true);
        from.Name = settings.FromName;
        message.From.Add(from);
        if (settings.ReplyTo.Length > 0 && MailboxAddress.TryParse(settings.ReplyTo, out var replyTo))
            message.ReplyTo.Add(replyTo);
        if (!MailboxAddress.TryParse(model.ToAddress, out var to))
            throw new MailSendException(string.Format(R("Web_MailServer_Error_Recipient"), model.ToAddress), permanent: true);
        message.To.Add(to);
        message.Subject = model.Subject;

        var body = new BodyBuilder { HtmlBody = model.HtmlBody, TextBody = model.TextBody };
        if (model.InlineLogo is { Length: > 0 } logo && model.InlineLogoContentType is { } type)
        {
            var resource = body.LinkedResources.Add("logo", logo, ContentType.Parse(type));
            resource.ContentId = MailTemplateRenderer.LogoContentId;
        }
        message.Body = body.ToMessageBody();
        return message;
    }

    private static SecureSocketOptions MapSecurity(SmtpSecurity security) => security switch
    {
        SmtpSecurity.None => SecureSocketOptions.None,
        SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
        SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        _ => SecureSocketOptions.Auto,
    };

    private static MailSendException Map(Exception ex, SmtpSettings settings) => ex switch
    {
        MailSendException mapped => mapped,
        AuthenticationException => new(R("Web_MailServer_Error_Auth"), false, ex, transport: true),
        SslHandshakeException => new(R("Web_MailServer_Error_Tls"), false, ex, transport: true),
        SmtpCommandException { ErrorCode: SmtpErrorCode.RecipientNotAccepted } cmd
            => new(string.Format(R("Web_MailServer_Error_Recipient"), cmd.Mailbox?.Address), true, ex),
        SmtpCommandException { ErrorCode: SmtpErrorCode.SenderNotAccepted }
            => new(R("Web_MailServer_Error_Sender"), false, ex, transport: true),
        SmtpCommandException cmd
            => new(string.Format(R("Web_MailServer_Error_Server"), (int)cmd.StatusCode, cmd.Message),
                // 5xx is a permanent rejection of this message; 4xx is worth a retry.
                (int)cmd.StatusCode >= 500 && cmd.ErrorCode == SmtpErrorCode.MessageNotAccepted, ex),
        SocketException or IOException when ex is not ProtocolException
            => new(string.Format(R("Web_MailServer_Error_Connect"), settings.Host, settings.Port), false, ex, transport: true),
        TimeoutException or OperationCanceledException => new(R("Web_MailServer_Error_Timeout"), false, ex, transport: true),
        _ => new(string.Format(R("Web_MailServer_Error_Server"), 0, ex.Message), false, ex),
    };

    private static string R(string key) => Resources.ResourceManager.GetString(key) ?? key;
}
