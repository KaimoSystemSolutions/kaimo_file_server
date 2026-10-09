using System.Text.Json;
using Kaimo_File_Server.Core.Domain.Notifications;
using C = Kaimo_File_Server.Core.Services.Notifications.NotificationEventCatalog;
using R = Kaimo_File_Server.Core.Services.Notifications.NotificationContextRoles;

namespace Kaimo_File_Server.Core.Services.Notifications;

/// <summary>
/// Central entry point for business events that may trigger mail notifications. Writes the
/// event to the outbox; the Web process dispatches it. Best-effort: never throws, so a
/// notification problem can never fail the business action that raised it.
/// </summary>
public interface INotificationPublisher
{
    Task PublishAsync(NotificationEvent notification, CancellationToken ct = default);
}

/// <summary>
/// Typed builders for the catalog events, so a call site stays one line. Payload values are
/// placeholder values for the templates and must never be secrets.
/// </summary>
public static class NotificationEvents
{
    public static NotificationEvent UserCreated(Guid userId, string displayName, string username, Guid? actorId)
        => Build(C.UserCreated, actorId, Roles((R.Affected, userId), (R.Actor, actorId)),
            [("user.displayName", displayName), ("user.username", username)]);

    public static NotificationEvent PasswordReset(Guid userId, string displayName, string username, Guid? actorId)
        => Build(C.UserPasswordReset, actorId, Roles((R.Affected, userId), (R.Actor, actorId)),
            [("user.displayName", displayName), ("user.username", username)]);

    public static NotificationEvent PasswordChanged(Guid userId, string displayName, string username)
        => Build(C.UserPasswordChanged, userId, Roles((R.Affected, userId)),
            [("user.displayName", displayName), ("user.username", username)]);

    /// <remarks>
    /// Locks of unknown user names share one dedup key: otherwise spraying invented names
    /// would send one mail per name to the security administrators despite the throttle.
    /// </remarks>
    public static NotificationEvent AccountLocked(Guid? userId, string username, string? remoteAddress, TimeSpan lockedFor)
        => Build(C.AccountLocked, null, Roles((R.Affected, userId)),
            [("user.username", username), ("remoteAddress", remoteAddress ?? "-"), ("lockedMinutes", Math.Max(1, (int)Math.Ceiling(lockedFor.TotalMinutes)).ToString())],
            userId is null ? "lock:unknown-user" : "lock:" + username.Trim().ToLowerInvariant());

    public static NotificationEvent UploadReceived(Guid linkId, string linkName, Guid creatorId, string fileName, long size)
        => Build(C.UploadReceived, null, Roles((R.LinkCreator, creatorId)),
            [("link.name", linkName), ("file.name", fileName), ("file.size", FormatSize(size))],
            "upload:" + linkId.ToString("N") + ":" + fileName);

    public static NotificationEvent LinkAccessed(Guid linkId, string linkName, Guid creatorId, string fileName)
        => Build(C.LinkAccessed, null, Roles((R.LinkCreator, creatorId)),
            [("link.name", linkName), ("file.name", fileName)],
            "access:" + linkId.ToString("N"));

    public static NotificationEvent SyncFailed(Guid definitionId, string syncName, Guid? creatorId, string errorCode)
        => Build(C.SyncFailed, null, Roles((R.SyncCreator, creatorId)),
            [("sync.name", syncName), ("errorCode", errorCode)],
            "sync:" + definitionId.ToString("N"));

    public static NotificationEvent SyncNeedsReauthorization(Guid connectionId, string syncName, Guid? creatorId, string errorCode)
        => Build(C.SyncNeedsReauthorization, null, Roles((R.SyncCreator, creatorId)),
            [("sync.name", syncName), ("errorCode", errorCode)],
            "reauth:" + connectionId.ToString("N"));

    public static NotificationEvent BackupFailed(string trigger, string error)
        => Build(C.BackupFailed, null, Roles(),
            [("backup.trigger", trigger), ("error", Truncate(error, 500))],
            "backup-failed");

    public static NotificationEvent BackupSucceeded(string trigger, string fileName, long size)
        => Build(C.BackupSucceeded, null, Roles(),
            [("backup.trigger", trigger), ("backup.fileName", fileName), ("backup.size", FormatSize(size))]);

    public static NotificationEvent FileBackupFailed(Guid jobId, string jobName, string repositoryName, Guid? creatorId, string errorCode, string failedSources)
        => Build(C.FileBackupFailed, null, Roles((R.JobCreator, creatorId)),
            [("job.name", jobName), ("repository.name", repositoryName), ("errorCode", errorCode), ("failedSources", Truncate(failedSources, 500))],
            "filebackup-failed:" + jobId.ToString("N"));

    public static NotificationEvent FileBackupWarnings(Guid jobId, string jobName, string repositoryName, Guid? creatorId, int warningCount)
        => Build(C.FileBackupWarnings, null, Roles((R.JobCreator, creatorId)),
            [("job.name", jobName), ("repository.name", repositoryName), ("warningCount", warningCount.ToString(System.Globalization.CultureInfo.InvariantCulture))],
            "filebackup-warnings:" + jobId.ToString("N"));

    public static NotificationEvent DeviceRegistered(Guid userId, string username, string deviceName, string platform)
        => Build(C.DeviceRegistered, userId, Roles((R.Affected, userId)),
            [("device.name", deviceName), ("device.platform", platform), ("user.username", username)]);

    public static NotificationEvent CertificateRenewed(DateTime expiresAtUtc)
        => Build(C.CertificateRenewed, null, Roles(),
            [("certificate.expiresAt", expiresAtUtc.ToString("yyyy-MM-dd"))]);

    public static NotificationEvent CertificateRenewalFailed(string error)
        => Build(C.CertificateRenewalFailed, null, Roles(),
            [("error", Truncate(error, 500))],
            "cert-renewal");

    private static NotificationEvent Build(
        string type, Guid? actorId, Dictionary<string, Guid> roles, (string Key, string Value)[] payload,
        string? dedupKey = null)
        => new()
        {
            Type = type,
            ActorUserId = actorId,
            DedupKey = dedupKey,
            SubjectUserIdsJson = JsonSerializer.Serialize(roles),
            PayloadJson = JsonSerializer.Serialize(payload.ToDictionary(p => p.Key, p => p.Value)),
        };

    private static Dictionary<string, Guid> Roles(params (string Role, Guid? UserId)[] roles)
        => roles.Where(r => r.UserId is { } id && id != Guid.Empty).ToDictionary(r => r.Role, r => r.UserId!.Value);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return size.ToString(unit == 0 ? "0" : "0.#", System.Globalization.CultureInfo.InvariantCulture) + " " + units[unit];
    }
}
