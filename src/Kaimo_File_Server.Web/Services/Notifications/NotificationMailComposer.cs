using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Infrastructure.Notifications;

namespace Kaimo_File_Server.Web.Services.Notifications;

/// <summary>Everything a mail needs besides the event: layout, server identity and default language.</summary>
public sealed record MailEnvironment(MailLayoutSettings Layout, string ServerName, string ServerUrl, string DefaultLanguage);

/// <summary>
/// Shared rendering path for the dispatcher, the SMTP test mail and the template editor
/// preview, so all three produce exactly the same mail.
/// </summary>
public sealed class NotificationMailComposer(
    IServiceScopeFactory scopeFactory,
    INotificationRepository repository,
    ISmtpConfigStore smtp,
    MailTemplateRenderer renderer)
{
    public async Task<MailEnvironment> LoadEnvironmentAsync()
    {
        using var scope = scopeFactory.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<IConfigRepository>();

        var layout = await config.GetFreshAsync(MailLayoutSettings.ConfigKey, MailLayoutSettings.Default());
        layout.Normalize();

        var serverUrl = layout.PublicBaseUrl;
        if (serverUrl.Length == 0)
        {
            var links = await config.GetAsync(ShareLinkSettings.ConfigKey, ShareLinkSettings.Default());
            links.Normalize();
            serverUrl = links.DefaultAddress ?? string.Empty;
        }

        var smtpSettings = await smtp.GetAsync();
        var serverName = string.IsNullOrWhiteSpace(smtpSettings.FromName) ? "Kaimo Files" : smtpSettings.FromName;
        var language = DefaultMailTemplates.NormalizeLanguage(await config.GetStringAsync("app.language", "de"));
        return new MailEnvironment(layout, serverName, serverUrl, language);
    }

    /// <summary>The administrator's customization of (event, language), else the built-in default.</summary>
    public async Task<MailTemplateSource> GetTemplateAsync(string eventType, string language, CancellationToken ct = default)
    {
        var custom = await repository.GetTemplateAsync(eventType, language, ct);
        return custom is null
            ? DefaultMailTemplates.Get(eventType, language)
            : new MailTemplateSource(custom.SubjectTemplate, custom.HtmlTemplate, custom.TextTemplate);
    }

    /// <summary>
    /// Renders one mail. <paramref name="values"/> are the event's placeholder values; the
    /// common ones (recipient, server, time) are added here. <paramref name="forPreview"/>
    /// embeds the logo as a data URI instead of an inline attachment reference.
    /// </summary>
    public Task<RenderedMail> RenderAsync(
        MailTemplateSource template,
        MailEnvironment environment,
        IReadOnlyDictionary<string, string> values,
        string language,
        string recipientName,
        string recipientAddress,
        DateTime occurredAtUtc,
        bool forPreview = false)
    {
        var all = new Dictionary<string, string>(values, StringComparer.Ordinal)
        {
            ["recipient.name"] = recipientName,
            ["recipient.email"] = recipientAddress,
            ["server.name"] = environment.ServerName,
            ["server.url"] = environment.ServerUrl,
            ["event.time"] = occurredAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
        };

        string? logo = null;
        if (environment.Layout.TryGetLogo(out _, out _))
            logo = forPreview ? environment.Layout.LogoDataUri : "cid:" + MailTemplateRenderer.LogoContentId;

        return renderer.RenderAsync(template, environment.Layout, all, language, logo);
    }

    /// <summary>The mail as handed to the SMTP transport, with the inline logo when referenced.</summary>
    public static MailMessageModel ToMessage(MailEnvironment environment, string to, string subject, string html, string text)
    {
        byte[]? logo = null;
        string? contentType = null;
        if (html.Contains("cid:" + MailTemplateRenderer.LogoContentId, StringComparison.Ordinal)
            && environment.Layout.TryGetLogo(out var bytes, out var type))
        {
            logo = bytes;
            contentType = type;
        }
        return new MailMessageModel(to, subject, html, text, logo, contentType);
    }
}
