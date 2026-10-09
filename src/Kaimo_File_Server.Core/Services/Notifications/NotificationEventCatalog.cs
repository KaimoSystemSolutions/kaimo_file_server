using Kaimo_File_Server.Core.Domain.Notifications;

namespace Kaimo_File_Server.Core.Services.Notifications;

/// <summary>A template placeholder of an event, with the sample value used by the editor preview.</summary>
public sealed record NotificationPlaceholder(string Name, string SampleValue);

/// <summary>
/// Static description of one notification event type. Drives the rules page, the template
/// editor, the placeholder picker and the default rules seeded on first start.
/// </summary>
/// <param name="Type">Stable key stored in the outbox and in rules, e.g. <c>user.created</c>.</param>
/// <param name="Category">Grouping key for the UI (<c>Notification_Category_{Category}</c>).</param>
/// <param name="ContextRoles">Context roles an event of this type can carry (see <see cref="NotificationContextRoles"/>).</param>
/// <param name="Placeholders">Event-specific placeholders; the common ones are in <see cref="NotificationEventCatalog.CommonPlaceholders"/>.</param>
/// <param name="DefaultRecipients">Recipients of the default rule seeded for this event.</param>
/// <param name="DefaultThrottleMinutes">Throttle of the default rule.</param>
public sealed record NotificationEventDefinition(
    string Type,
    string Category,
    IReadOnlyList<string> ContextRoles,
    IReadOnlyList<NotificationPlaceholder> Placeholders,
    IReadOnlyList<RecipientSpec> DefaultRecipients,
    int DefaultThrottleMinutes = 0)
{
    /// <summary>Resource key of the display name; <c>…_Desc</c> is the description.</summary>
    public string NameKey => "Notification_Event_" + Type.Replace('.', '_');
    public string DescriptionKey => NameKey + "_Desc";
}

/// <summary>Context role names an event can carry in <see cref="NotificationEvent.SubjectUserIdsJson"/>.</summary>
public static class NotificationContextRoles
{
    /// <summary>The user the event is about (created user, locked account, device owner).</summary>
    public const string Affected = "affected";

    /// <summary>The user who triggered the event.</summary>
    public const string Actor = "actor";

    /// <summary>The creator of a public share/upload link.</summary>
    public const string LinkCreator = "linkCreator";

    /// <summary>The creator of a cloud sync definition.</summary>
    public const string SyncCreator = "syncCreator";

    /// <summary>The creator of a file backup job.</summary>
    public const string JobCreator = "jobCreator";
}

/// <summary>The in-code catalog of all notification event types.</summary>
public static class NotificationEventCatalog
{
    public const string UserCreated = "user.created";
    public const string UserPasswordReset = "user.password_reset";
    public const string UserPasswordChanged = "user.password_changed";
    public const string AccountLocked = "security.account_locked";
    public const string UploadReceived = "sharelink.upload_received";
    public const string LinkAccessed = "sharelink.accessed";
    public const string SyncFailed = "sync.failed";
    public const string SyncNeedsReauthorization = "sync.needs_reauthorization";
    public const string BackupFailed = "backup.failed";
    public const string BackupSucceeded = "backup.succeeded";
    public const string FileBackupFailed = "backup.restic.failed";
    public const string FileBackupWarnings = "backup.restic.warnings";
    public const string DeviceRegistered = "device.registered";
    public const string CertificateRenewed = "certificate.renewed";
    public const string CertificateRenewalFailed = "certificate.renewal_failed";

    /// <summary>Placeholders available in every template, in addition to the event's own.</summary>
    public static readonly IReadOnlyList<NotificationPlaceholder> CommonPlaceholders =
    [
        new("recipient.name", "Erika Mustermann"),
        new("recipient.email", "erika@example.com"),
        new("server.name", "Kaimo Files"),
        new("server.url", "https://files.example.com"),
        new("event.time", "2026-09-28 14:05"),
    ];

    private static RecipientSpec Role(string role) => new(RecipientKind.ContextRole, role);
    private static RecipientSpec Holders(string permission) => new(RecipientKind.PermissionHolder, permission);
    private static NotificationPlaceholder P(string name, string sample) => new(name, sample);

    public static readonly IReadOnlyList<NotificationEventDefinition> All =
    [
        new(UserCreated, "users", [NotificationContextRoles.Affected, NotificationContextRoles.Actor],
            [P("user.displayName", "Erika Mustermann"), P("user.username", "erika")],
            [Role(NotificationContextRoles.Affected)]),
        new(UserPasswordReset, "users", [NotificationContextRoles.Affected, NotificationContextRoles.Actor],
            [P("user.displayName", "Erika Mustermann"), P("user.username", "erika")],
            [Role(NotificationContextRoles.Affected)]),
        new(UserPasswordChanged, "users", [NotificationContextRoles.Affected],
            [P("user.displayName", "Erika Mustermann"), P("user.username", "erika")],
            [Role(NotificationContextRoles.Affected)]),
        new(AccountLocked, "security", [NotificationContextRoles.Affected],
            [P("user.username", "erika"), P("remoteAddress", "203.0.113.7"), P("lockedMinutes", "15")],
            [Role(NotificationContextRoles.Affected), Holders("ViewSecurityMonitor")],
            DefaultThrottleMinutes: 60),
        new(UploadReceived, "sharelinks", [NotificationContextRoles.LinkCreator],
            [P("link.name", "Bewerbungen"), P("file.name", "lebenslauf.pdf"), P("file.size", "1.2 MB")],
            [Role(NotificationContextRoles.LinkCreator)]),
        new(LinkAccessed, "sharelinks", [NotificationContextRoles.LinkCreator],
            [P("link.name", "Projektplan"), P("file.name", "projektplan.zip")],
            [Role(NotificationContextRoles.LinkCreator)],
            DefaultThrottleMinutes: 60),
        new(SyncFailed, "sync", [NotificationContextRoles.SyncCreator],
            [P("sync.name", "OneDrive Marketing"), P("errorCode", "remote_path_missing")],
            [Role(NotificationContextRoles.SyncCreator), Holders("SyncAdmin")],
            DefaultThrottleMinutes: 360),
        new(SyncNeedsReauthorization, "sync", [NotificationContextRoles.SyncCreator],
            [P("sync.name", "OneDrive Marketing"), P("errorCode", "invalid_grant")],
            [Role(NotificationContextRoles.SyncCreator), Holders("SyncAdmin")],
            DefaultThrottleMinutes: 1440),
        new(BackupFailed, "backup", [],
            [P("backup.trigger", "Scheduled"), P("error", "pg_dump failed with exit code 1")],
            [Holders("ManageBackups")],
            DefaultThrottleMinutes: 360),
        new(BackupSucceeded, "backup", [],
            [P("backup.trigger", "Scheduled"), P("backup.fileName", "kaimo_2026-09-28_0200_scheduled.dump"), P("backup.size", "48.3 MB")],
            [Holders("ManageBackups")]),
        new(FileBackupFailed, "filebackup", [NotificationContextRoles.JobCreator],
            [P("job.name", "Projekte nachts"), P("repository.name", "NAS Keller"), P("errorCode", "repo_unreachable"), P("failedSources", "Projekte")],
            [Role(NotificationContextRoles.JobCreator), Holders("ManageBackupJobs")],
            DefaultThrottleMinutes: 360),
        new(FileBackupWarnings, "filebackup", [NotificationContextRoles.JobCreator],
            [P("job.name", "Projekte nachts"), P("repository.name", "NAS Keller"), P("warningCount", "3")],
            [Role(NotificationContextRoles.JobCreator), Holders("ManageBackupJobs")],
            DefaultThrottleMinutes: 1440),
        new(DeviceRegistered, "devices", [NotificationContextRoles.Affected],
            [P("device.name", "Erikas iPhone"), P("device.platform", "ios"), P("user.username", "erika")],
            [Role(NotificationContextRoles.Affected)]),
        new(CertificateRenewed, "certificates", [],
            [P("certificate.expiresAt", "2027-09-28")],
            [Holders("ManageCertificates")]),
        new(CertificateRenewalFailed, "certificates", [],
            [P("error", "Access to the certificate store was denied.")],
            [Holders("ManageCertificates")],
            DefaultThrottleMinutes: 1440),
    ];

    private static readonly Dictionary<string, NotificationEventDefinition> ByType =
        All.ToDictionary(d => d.Type, StringComparer.Ordinal);

    public static NotificationEventDefinition? Find(string type)
        => ByType.GetValueOrDefault(type);

    /// <summary>Sample values for the preview: common placeholders plus the event's own.</summary>
    public static Dictionary<string, string> SamplePayload(NotificationEventDefinition definition)
        => CommonPlaceholders.Concat(definition.Placeholders).ToDictionary(p => p.Name, p => p.SampleValue);
}
