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
