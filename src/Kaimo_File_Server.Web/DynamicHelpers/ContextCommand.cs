using Kaimo_File_Server.Core.Language;

namespace Kaimo_File_Server.Web.DynamicHelpers;

/// <summary>
/// Pure metadata describing a context-menu command. Shared by the settings GUI
/// (which lets an admin arrange commands per <see cref="ContextMenuScope"/>) and
/// the menu renderer (which maps the <see cref="Id"/> to a concrete action).
/// Contains no action — the action is built in <c>ContextMenu.razor</c>.
/// </summary>
public record ContextCommand(
    string Id,
    Func<string> Label,
    string Icon,
    bool IsDanger = false,
    SvgOptions? IconOptions = null,
    ContextMenuScope[]? ValidScopes = null)
{
    /// <summary>Resolves the localized label at call time (culture-safe).</summary>
    public string DisplayLabel => Label();

    public SvgOptions ResolvedIconOptions => IconOptions ?? new SvgOptions(Width: 14, Height: 14);

    /// <summary>True if this command may be placed in the given scope.</summary>
    public bool IsValidFor(ContextMenuScope scope) =>
        ValidScopes is null || ValidScopes.Contains(scope);
}

/// <summary>
/// The fixed catalog of all context-menu commands. The stable string
/// <see cref="ContextCommand.Id"/> values are persisted in
/// <see cref="ContextMenuConfig"/> and mapped to actions in the menu renderer.
/// </summary>
public static class ContextCommandCatalog
{
    // -- Scope groupings --
    private static readonly ContextMenuScope[] AllSingle =
    {
        ContextMenuScope.Folder, ContextMenuScope.Archive, ContextMenuScope.Image,
        ContextMenuScope.Video, ContextMenuScope.Audio, ContextMenuScope.Document,
        ContextMenuScope.OtherFile,
    };

    // Single files/folders that can be compressed (an archive isn't re-compressed).
    private static readonly ContextMenuScope[] Compressible =
    {
        ContextMenuScope.Folder, ContextMenuScope.Image, ContextMenuScope.Video,
        ContextMenuScope.Audio, ContextMenuScope.Document, ContextMenuScope.OtherFile,
        ContextMenuScope.MultiSelection,
    };

    private static readonly ContextMenuScope[] SingleAndMulti =
        AllSingle.Append(ContextMenuScope.MultiSelection).ToArray();

    private static readonly ContextMenuScope[] EveryScope =
        Enum.GetValues<ContextMenuScope>();

    public static readonly IReadOnlyList<ContextCommand> All = new[]
    {
        new ContextCommand("open", () => Resources.Context_Menu_Open, "arrow_right.svg",
            ValidScopes: AllSingle),

        new ContextCommand("extract", () => Resources.Context_Menu_Extract, "file_zip.svg",
            ValidScopes: new[] { ContextMenuScope.Archive, ContextMenuScope.MultiSelection }),

        new ContextCommand("compress.zip", () => Resources.Context_Menu_CompressZip, "archive.svg",
            ValidScopes: Compressible),

        new ContextCommand("compress.targz", () => Resources.Context_Menu_CompressTarGz, "archive.svg",
            ValidScopes: Compressible),

        new ContextCommand("versions", () => Resources.Context_Menu_Versions, "history.svg",
            ValidScopes: AllSingle),

        new ContextCommand("rename", () => Resources.Context_Menu_Rename, "edit.svg",
            ValidScopes: AllSingle),

        new ContextCommand("delete", () => Resources.Context_Menu_Delete, "trash.svg",
            IsDanger: true, ValidScopes: SingleAndMulti),

        new ContextCommand("permissions", () => Resources.Context_Menu_Permissions, "shield.svg",
            IconOptions: new SvgOptions(Width: 12, Height: 12, StrokeWidth: 2, CssClass: "acl-indicator-icon"),
            ValidScopes: AllSingle),

        new ContextCommand("properties", () => Resources.Context_Menu_Properties, "info.svg",
            ValidScopes: AllSingle),

        new ContextCommand("newfolder", () => Resources.Context_Menu_NewFolder, "folder.svg",
            ValidScopes: EveryScope),

        new ContextCommand("refresh", () => Resources.Context_Menu_Refresh, "refresh.svg",
            ValidScopes: EveryScope),
    };

    /// <summary>The "general" commands rendered as a trailing group (auto-separator).</summary>
    public static readonly HashSet<string> GeneralIds = new() { "newfolder", "refresh" };

    public static ContextCommand? ById(string id) => All.FirstOrDefault(c => c.Id == id);

    /// <summary>All commands that may be placed in the given scope.</summary>
    public static IEnumerable<ContextCommand> ForScope(ContextMenuScope scope) =>
        All.Where(c => c.IsValidFor(scope));
}
