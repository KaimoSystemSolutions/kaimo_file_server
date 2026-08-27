namespace Kaimo_File_Server.Web.Components.Shared;

/// <summary>
/// Semantic tone for a <see cref="StatusBadge"/>. The tone drives the colour of
/// the pill (border, fill and dot); the caller supplies the localized label and
/// picks the tone that fits the use case. Keeping the vocabulary small is what
/// makes the status labels consistent across pages.
/// </summary>
public enum StatusTone
{
    /// <summary>Healthy / affirmative state — e.g. Active, Available, Healthy, Ready.</summary>
    Positive,

    /// <summary>Problem / blocking state — e.g. Disabled, Revoked, Unavailable.</summary>
    Negative,

    /// <summary>Needs attention but not broken — e.g. Attention required.</summary>
    Warning,

    /// <summary>Informational / off state carrying no judgement — e.g. a disabled toggle.</summary>
    Neutral
}
