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
