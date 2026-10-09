using Kaimo_File_Server.Core.Services.Notifications;
using C = Kaimo_File_Server.Core.Services.Notifications.NotificationEventCatalog;

namespace Kaimo_File_Server.Infrastructure.Notifications;

/// <summary>A subject + HTML body pair of Liquid sources.</summary>
public sealed record MailTemplateSource(string Subject, string Html, string? Text = null);

/// <summary>
/// Built-in mail templates. They apply whenever an administrator has not customized the
/// template of an event/language, so they can improve with updates without a migration.
/// Never put secrets into a template: the welcome mail links to the login page only.
/// </summary>
public static class DefaultMailTemplates
{
    public static readonly string[] Languages = ["de", "en"];

    /// <summary>The shared frame. <c>header</c>, <c>content</c> and <c>footer</c> are pre-rendered HTML.</summary>
    public const string Layout = """
        <!DOCTYPE html>
        <html lang="{{ layout.language }}">
        <head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>{{ subject }}</title></head>
        <body style="margin:0;padding:0;background:#EEF1EC;font-family:Segoe UI,Helvetica,Arial,sans-serif;color:#1B201C;">
          <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:#EEF1EC;padding:24px 12px;">
            <tr><td align="center">
              <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:600px;background:#FFFFFF;border:1px solid #CCD1CB;border-top:4px solid {{ layout.accent }};border-radius:4px;">
                <tr><td style="padding:20px 28px 8px;">
                  {% if layout.logo != empty %}<img src="{{ layout.logo }}" alt="{{ server.name }}" style="max-height:48px;max-width:200px;display:block;margin-bottom:8px;">{% endif %}
                  {{ header | raw }}
                </td></tr>
                <tr><td style="padding:8px 28px 24px;font-size:15px;line-height:1.55;">{{ content | raw }}</td></tr>
                <tr><td style="padding:16px 28px;border-top:1px solid #CCD1CB;font-size:12px;color:#707872;">{{ footer | raw }}</td></tr>
              </table>
            </td></tr>
          </table>
        </body>
        </html>
        """;

    private const string LoginLink = """<p><a href="{{ server.url }}" style="color:{{ layout.accent }};font-weight:600;">{{ server.url }}</a></p>""";

    private static readonly Dictionary<(string Type, string Language), MailTemplateSource> Templates = new()
    {
        [(C.UserCreated, "de")] = new("Willkommen bei {{ server.name }}",
            "<p>Hallo {{ user.displayName }},</p><p>für dich wurde ein Konto bei {{ server.name }} angelegt. Dein Benutzername lautet <strong>{{ user.username }}</strong>. Das Passwort erhältst du von deinem Administrator.</p>" + LoginLink),
        [(C.UserCreated, "en")] = new("Welcome to {{ server.name }}",
            "<p>Hello {{ user.displayName }},</p><p>an account has been created for you on {{ server.name }}. Your user name is <strong>{{ user.username }}</strong>. Your administrator will give you the password.</p>" + LoginLink),

        [(C.UserPasswordReset, "de")] = new("Dein Passwort wurde zurückgesetzt",
            "<p>Hallo {{ user.displayName }},</p><p>ein Administrator hat das Passwort deines Kontos <strong>{{ user.username }}</strong> zurückgesetzt. Alle anderen Sitzungen wurden beendet.</p>" + LoginLink),
        [(C.UserPasswordReset, "en")] = new("Your password was reset",
            "<p>Hello {{ user.displayName }},</p><p>an administrator reset the password of your account <strong>{{ user.username }}</strong>. All other sessions were signed out.</p>" + LoginLink),

        [(C.UserPasswordChanged, "de")] = new("Dein Passwort wurde geändert",
            "<p>Hallo {{ user.displayName }},</p><p>das Passwort deines Kontos <strong>{{ user.username }}</strong> wurde am {{ event.time }} geändert. Warst du das nicht, wende dich bitte sofort an deinen Administrator.</p>"),
        [(C.UserPasswordChanged, "en")] = new("Your password was changed",
            "<p>Hello {{ user.displayName }},</p><p>the password of your account <strong>{{ user.username }}</strong> was changed on {{ event.time }}. If this was not you, contact your administrator immediately.</p>"),

        [(C.AccountLocked, "de")] = new("Konto {{ user.username }} vorübergehend gesperrt",
            "<p>Nach zu vielen fehlgeschlagenen Anmeldeversuchen wurde das Konto <strong>{{ user.username }}</strong> für {{ lockedMinutes }} Minuten gesperrt.</p><p>Letzte Quelle: {{ remoteAddress }}<br>Zeitpunkt: {{ event.time }}</p>"),
        [(C.AccountLocked, "en")] = new("Account {{ user.username }} temporarily locked",
            "<p>After too many failed sign-in attempts the account <strong>{{ user.username }}</strong> was locked for {{ lockedMinutes }} minutes.</p><p>Last source: {{ remoteAddress }}<br>Time: {{ event.time }}</p>"),

        [(C.UploadReceived, "de")] = new("Neue Datei über „{{ link.name }}“",
            "<p>Hallo {{ recipient.name }},</p><p>über deinen Upload-Link <strong>{{ link.name }}</strong> wurde die Datei <strong>{{ file.name }}</strong> ({{ file.size }}) hochgeladen.</p>" + LoginLink),
        [(C.UploadReceived, "en")] = new("New file via \"{{ link.name }}\"",
            "<p>Hello {{ recipient.name }},</p><p>the file <strong>{{ file.name }}</strong> ({{ file.size }}) was uploaded through your upload link <strong>{{ link.name }}</strong>.</p>" + LoginLink),

        [(C.LinkAccessed, "de")] = new("Freigabe-Link „{{ link.name }}“ wurde abgerufen",
            "<p>Hallo {{ recipient.name }},</p><p>über deinen Freigabe-Link <strong>{{ link.name }}</strong> wurde <strong>{{ file.name }}</strong> heruntergeladen ({{ event.time }}).</p>"),
        [(C.LinkAccessed, "en")] = new("Share link \"{{ link.name }}\" was accessed",
            "<p>Hello {{ recipient.name }},</p><p><strong>{{ file.name }}</strong> was downloaded through your share link <strong>{{ link.name }}</strong> ({{ event.time }}).</p>"),

        [(C.SyncFailed, "de")] = new("Synchronisation „{{ sync.name }}“ fehlgeschlagen",
            "<p>Die Synchronisation <strong>{{ sync.name }}</strong> ist fehlgeschlagen.</p><p>Fehlercode: <code>{{ errorCode }}</code><br>Zeitpunkt: {{ event.time }}</p>" + LoginLink),
        [(C.SyncFailed, "en")] = new("Sync \"{{ sync.name }}\" failed",
            "<p>The sync <strong>{{ sync.name }}</strong> failed.</p><p>Error code: <code>{{ errorCode }}</code><br>Time: {{ event.time }}</p>" + LoginLink),

        [(C.SyncNeedsReauthorization, "de")] = new("Synchronisation „{{ sync.name }}“ muss neu autorisiert werden",
            "<p>Der Zugriff der Synchronisation <strong>{{ sync.name }}</strong> auf den externen Speicher wurde abgelehnt (<code>{{ errorCode }}</code>). Die Verbindung muss in den Einstellungen neu autorisiert werden; bis dahin ruht die Synchronisation.</p>" + LoginLink),
        [(C.SyncNeedsReauthorization, "en")] = new("Sync \"{{ sync.name }}\" needs to be re-authorized",
            "<p>The external storage rejected the access of the sync <strong>{{ sync.name }}</strong> (<code>{{ errorCode }}</code>). Re-authorize the connection in the settings; the sync is paused until then.</p>" + LoginLink),

        [(C.BackupFailed, "de")] = new("Datenbank-Backup fehlgeschlagen",
            "<p>Ein Datenbank-Backup ({{ backup.trigger }}) ist am {{ event.time }} fehlgeschlagen.</p><p><code>{{ error }}</code></p>"),
        [(C.BackupFailed, "en")] = new("Database backup failed",
            "<p>A database backup ({{ backup.trigger }}) failed on {{ event.time }}.</p><p><code>{{ error }}</code></p>"),

        [(C.BackupSucceeded, "de")] = new("Datenbank-Backup erstellt",
            "<p>Das Datenbank-Backup <strong>{{ backup.fileName }}</strong> ({{ backup.size }}, {{ backup.trigger }}) wurde erstellt.</p>"),
        [(C.BackupSucceeded, "en")] = new("Database backup created",
            "<p>The database backup <strong>{{ backup.fileName }}</strong> ({{ backup.size }}, {{ backup.trigger }}) was created.</p>"),

        [(C.FileBackupFailed, "de")] = new("Datensicherung „{{ job.name }}“ fehlgeschlagen",
            "<p>Der Sicherungsauftrag <strong>{{ job.name }}</strong> in das Repository <strong>{{ repository.name }}</strong> ist fehlgeschlagen.</p><p>Fehlercode: <code>{{ errorCode }}</code><br>Betroffene Quellen: {{ failedSources }}<br>Zeitpunkt: {{ event.time }}</p>" + LoginLink),
        [(C.FileBackupFailed, "en")] = new("File backup \"{{ job.name }}\" failed",
            "<p>The backup job <strong>{{ job.name }}</strong> into the repository <strong>{{ repository.name }}</strong> failed.</p><p>Error code: <code>{{ errorCode }}</code><br>Affected sources: {{ failedSources }}<br>Time: {{ event.time }}</p>" + LoginLink),

        [(C.FileBackupWarnings, "de")] = new("Datensicherung „{{ job.name }}“ mit Warnungen",
            "<p>Der Sicherungsauftrag <strong>{{ job.name }}</strong> wurde abgeschlossen, aber {{ warningCount }} Dateien konnten nicht gelesen werden (z. B. weil sie während der Sicherung geändert oder gesperrt waren). Details stehen im Verlauf der Sicherung.</p>" + LoginLink),
        [(C.FileBackupWarnings, "en")] = new("File backup \"{{ job.name }}\" completed with warnings",
            "<p>The backup job <strong>{{ job.name }}</strong> completed, but {{ warningCount }} files could not be read (for example because they changed or were locked during the backup). See the backup activity for details.</p>" + LoginLink),

        [(C.DeviceRegistered, "de")] = new("Neues Gerät angemeldet: {{ device.name }}",
            "<p>Hallo {{ recipient.name }},</p><p>mit deinem Konto <strong>{{ user.username }}</strong> wurde ein neues Gerät angemeldet: <strong>{{ device.name }}</strong> ({{ device.platform }}), {{ event.time }}.</p><p>Warst du das nicht, ändere bitte dein Passwort und informiere deinen Administrator.</p>"),
        [(C.DeviceRegistered, "en")] = new("New device signed in: {{ device.name }}",
            "<p>Hello {{ recipient.name }},</p><p>a new device signed in with your account <strong>{{ user.username }}</strong>: <strong>{{ device.name }}</strong> ({{ device.platform }}), {{ event.time }}.</p><p>If this was not you, change your password and inform your administrator.</p>"),

        [(C.CertificateRenewed, "de")] = new("HTTPS-Zertifikat erneuert",
            "<p>Das selbstsignierte HTTPS-Zertifikat von {{ server.name }} wurde erneuert. Es ist gültig bis {{ certificate.expiresAt }}.</p><p>Clients, die dem alten Zertifikat vertraut haben, müssen dem neuen erneut vertrauen.</p>"),
        [(C.CertificateRenewed, "en")] = new("HTTPS certificate renewed",
            "<p>The self-signed HTTPS certificate of {{ server.name }} was renewed. It is valid until {{ certificate.expiresAt }}.</p><p>Clients that trusted the old certificate need to trust the new one.</p>"),

        [(C.CertificateRenewalFailed, "de")] = new("Erneuerung des HTTPS-Zertifikats fehlgeschlagen",
            "<p>Das HTTPS-Zertifikat von {{ server.name }} konnte nicht erneuert werden.</p><p><code>{{ error }}</code></p>"),
        [(C.CertificateRenewalFailed, "en")] = new("HTTPS certificate renewal failed",
            "<p>The HTTPS certificate of {{ server.name }} could not be renewed.</p><p><code>{{ error }}</code></p>"),
    };

    /// <summary>The built-in template, falling back to German and then to a generic text.</summary>
    public static MailTemplateSource Get(string eventType, string language)
        => Templates.GetValueOrDefault((eventType, language))
           ?? Templates.GetValueOrDefault((eventType, "de"))
           ?? new MailTemplateSource(eventType, "<p>" + eventType + "</p>");

    /// <summary>Body of the SMTP test mail (not an outbox event).</summary>
    public static MailTemplateSource TestMail(string language) => language == "en"
        ? new("Test mail from {{ server.name }}", "<p>This is a test mail from {{ server.name }}. The mail server settings work.</p>")
        : new("Testmail von {{ server.name }}", "<p>Dies ist eine Testmail von {{ server.name }}. Die Mailserver-Einstellungen funktionieren.</p>");

    /// <summary>Normalizes to a supported language code (<c>de</c> fallback).</summary>
    public static string NormalizeLanguage(string? language)
    {
        var code = (language ?? string.Empty).Trim().ToLowerInvariant();
        if (code.Length > 2) code = code[..2];
        return Languages.Contains(code) ? code : "de";
    }

    /// <summary>Whether every catalog event has a built-in template in every language (checked by a test).</summary>
    internal static bool CoversCatalog()
        => NotificationEventCatalog.All.All(d => Languages.All(l => Templates.ContainsKey((d.Type, l))));
}
