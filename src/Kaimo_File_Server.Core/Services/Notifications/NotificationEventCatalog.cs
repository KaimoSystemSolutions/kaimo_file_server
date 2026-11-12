using Kaimo_File_Server.Core.Domain.Notifications;

namespace Kaimo_File_Server.Core.Services.Notifications;

/// <summary>The in-code catalog of all notification event types.</summary>
public static class NotificationEventCatalog
{
    public const string UserCreated = "user.created";
    public const string UserPasswordReset = "user.password_reset";
    public const string UserPasswordChanged = "user.password_changed";
    public const string AccountLocked = "security.account_locked";
    public const string AddressLocked = "security.address_locked";
    public const string SecondFactorChanged = "security.second_factor_changed";
    public const string SecondFactorFailed = "security.second_factor_failed";
    public const string UploadReceived = "sharelink.upload_received";
    public const string LinkAccessed = "sharelink.accessed";
    public const string SyncFailed = "sync.failed";
    public const string SyncNeedsReauthorization = "sync.needs_reauthorization";
    public const string BackupFailed = "backup.failed";
    public const string BackupSucceeded = "backup.succeeded";
    public const string FileBackupFailed = "backup.restic.failed";
    public const string FileBackupSucceeded = "backup.restic.succeeded";
    public const string FileBackupWarnings = "backup.restic.warnings";
    public const string FileBackupStale = "backup.restic.stale";
    public const string FileBackupCheckFailed = "backup.restic.check_failed";
    public const string FileBackupTestRestoreMismatch = "backup.restic.test_restore_mismatch";
    public const string FileBackupRepositoryUnreachable = "backup.restic.repo_unreachable";
    public const string FileBackupMaintenanceFailed = "backup.restic.maintenance_failed";
    public const string DeviceRegistered = "device.registered";
    public const string CertificateRenewed = "certificate.renewed";
    public const string CertificateRenewalFailed = "certificate.renewal_failed";

    /// <summary>Placeholders available in every template, in addition to the event's own.</summary>
    public static readonly IReadOnlyList<NotificationPlaceholder> CommonPlaceholders =
    [
        new("recipient.name", "Erika Mustermann"),
        new("recipient.email", "erika@example.com"),
        new("server.name", "Kaimo File Server"),
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
        // change: enabled | disabled | reset | recovery_code_used | recovery_codes_regenerated
        new(SecondFactorChanged, "security", [NotificationContextRoles.Affected, NotificationContextRoles.Actor],
            [P("user.displayName", "Erika Mustermann"), P("user.username", "erika"), P("change", "enabled")],
            [Role(NotificationContextRoles.Affected)]),
        new(SecondFactorFailed, "security", [NotificationContextRoles.Affected],
            [P("user.username", "erika"), P("remoteAddress", "203.0.113.7")],
            [Role(NotificationContextRoles.Affected), Holders("ViewSecurityMonitor")],
            DefaultThrottleMinutes: 60),
        new(AddressLocked, "security", [],
            [P("remoteAddress", "203.0.113.7"), P("lockedMinutes", "30"), P("channel", "WebDAV")],
            [Holders("ViewSecurityMonitor")],
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
        new(FileBackupSucceeded, "filebackup", [NotificationContextRoles.JobCreator],
            [P("job.name", "Projekte nachts"), P("repository.name", "NAS Keller"), P("backup.trigger", "Scheduled"),
             P("filesNew", "128"), P("filesChanged", "17"), P("dataAdded", "1.4 GB"), P("duration", "00:12:34")],
            [Role(NotificationContextRoles.JobCreator), Holders("ManageBackupJobs")]),
        new(FileBackupWarnings, "filebackup", [NotificationContextRoles.JobCreator],
            [P("job.name", "Projekte nachts"), P("repository.name", "NAS Keller"), P("warningCount", "3")],
            [Role(NotificationContextRoles.JobCreator), Holders("ManageBackupJobs")],
            DefaultThrottleMinutes: 1440),
        new(FileBackupStale, "filebackup", [NotificationContextRoles.JobCreator],
            [P("job.name", "Projekte nachts"), P("repository.name", "NAS Keller"), P("lastSuccess", "2026-09-25 02:04"), P("days", "3")],
            [Role(NotificationContextRoles.JobCreator), Holders("ManageBackupJobs")],
            DefaultThrottleMinutes: 1440),
        new(FileBackupCheckFailed, "filebackup", [],
            [P("repository.name", "NAS Keller"), P("errorCount", "2")],
            [Holders("ManageBackupRepositories")],
            DefaultThrottleMinutes: 1440),
        new(FileBackupTestRestoreMismatch, "filebackup", [],
            [P("repository.name", "NAS Keller"), P("mismatchCount", "1"), P("files", "/data/storage/pool01/projekte/plan.xlsx")],
            [Holders("ManageBackupRepositories")],
            DefaultThrottleMinutes: 1440),
        new(FileBackupRepositoryUnreachable, "filebackup", [],
            [P("repository.name", "NAS Keller"), P("errorCode", "repo_unreachable")],
            [Holders("ManageBackupRepositories")],
            DefaultThrottleMinutes: 360),
        new(FileBackupMaintenanceFailed, "filebackup", [],
            [P("repository.name", "NAS Keller"), P("step", "Prune"), P("errorCode", "lock_failed")],
            [Holders("ManageBackupRepositories")],
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
