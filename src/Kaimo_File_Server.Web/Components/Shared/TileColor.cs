namespace Kaimo_File_Server.Web.Components.Shared;

/// <summary>
/// Colour of a tile icon (<see cref="FactTile"/>, <see cref="ActionTile"/>) or of a
/// <see cref="FactTileSegment"/>. Category colours mark an area (storage = folder, people =
/// user, …) and map to existing tokens (<c>--folder-*</c>, <c>--entity-*</c>, <c>--info</c>);
/// the status colours are for health segments only. CSS: <c>.tile-icon--*</c> and
/// <c>.tile-segment--*</c> in app.css.
/// </summary>
public enum TileColor
{
    Neutral,
    Folder,
    User,
    Group,
    Role,
    Department,
    Info,
    Accent,
    Success,
    Warning,
    Danger
}

/// <summary>One part of a <see cref="FactTile"/> distribution bar, e.g. "6 healthy".</summary>
public sealed record FactTileSegment(int Count, string Label, TileColor Color);
