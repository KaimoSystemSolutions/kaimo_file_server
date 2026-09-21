namespace Kaimo_File_Server.Web.DynamicHelpers;

/// <summary>
/// The globally configured context-menu layout, persisted under the config key
/// <see cref="ConfigKey"/> via <c>IConfigRepository</c>. Maps each
/// <see cref="ContextMenuScope"/> (by name) to an ordered list of command ids
/// from <see cref="ContextCommandCatalog"/>.
/// </summary>
public class ContextMenuConfig
{
    public const string ConfigKey = "contextmenu.layout";

    /// <summary>Scope name → ordered command ids.</summary>
    public Dictionary<string, List<string>> Menus { get; set; } = new();

    /// <summary>
    /// The single global command order the editor works in. Every scope's menu is
    /// <c>Order ∩ (its assigned commands)</c>. Persisted alongside <see cref="Menus"/>;
    /// null in configs saved before the matrix editor existed (then
    /// <see cref="DefaultOrder"/> is used and <see cref="Menus"/> still renders as-is).
    /// </summary>
    public List<string>? Order { get; set; }

    /// <summary>
    /// The canonical command order used when no <see cref="Order"/> is stored. Matches
    /// the relative order of the out-of-the-box per-scope menus in <see cref="DefaultForScope"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultOrder = new[]
    {
        "open", "download", "sharelink", "hash", "extract", "compress.zip", "compress.targz",
        "rename", "versions", "delete", "recycle.empty", "permissions", "properties",
        "newfolder", "refresh",
    };

    /// <summary>Ordered command ids for a scope, falling back to the default layout.</summary>
    public List<string> ForScope(ContextMenuScope scope)
    {
        if (Menus.TryGetValue(scope.ToString(), out var ids) && ids is not null)
            return ids;
        return DefaultForScope(scope);
    }

    public void SetScope(ContextMenuScope scope, List<string> ids)
        => Menus[scope.ToString()] = ids;

    /// <summary>
    /// The out-of-the-box layout, reproducing the previously hard-coded menu so
    /// behaviour is unchanged when no configuration has been saved yet.
    /// </summary>
    public static ContextMenuConfig Default()
    {
        var cfg = new ContextMenuConfig();
        foreach (ContextMenuScope scope in Enum.GetValues<ContextMenuScope>())
            cfg.Menus[scope.ToString()] = DefaultForScope(scope);
        return cfg;
    }

    public static List<string> DefaultForScope(ContextMenuScope scope) => scope switch
    {
        ContextMenuScope.Background => new() { "newfolder", "refresh" },

        ContextMenuScope.Folder => new()
            { "open", "download", "sharelink", "compress.zip", "compress.targz", "rename", "versions", "delete", "recycle.empty", "permissions", "properties", "newfolder", "refresh" },

        ContextMenuScope.Archive => new()
            { "open", "download", "sharelink", "hash", "extract", "rename", "versions", "delete", "permissions", "properties", "newfolder", "refresh" },

        ContextMenuScope.MultiSelection => new()
            { "download", "extract", "compress.zip", "compress.targz", "delete", "newfolder", "refresh" },

        // Image / Video / Audio / Document / OtherFile share the regular-file layout.
        _ => new()
            { "open", "download", "sharelink", "hash", "compress.zip", "compress.targz", "rename", "versions", "delete", "permissions", "properties", "newfolder", "refresh" },
    };
}
