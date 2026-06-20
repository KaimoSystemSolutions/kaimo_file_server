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
            { "open", "compress.zip", "compress.targz", "rename", "delete", "permissions", "newfolder", "refresh" },

        ContextMenuScope.Archive => new()
            { "open", "extract", "rename", "delete", "permissions", "newfolder", "refresh" },

        ContextMenuScope.MultiSelection => new()
            { "extract", "compress.zip", "compress.targz", "delete", "newfolder", "refresh" },

        // Image / Video / Audio / Document / OtherFile share the regular-file layout.
        _ => new()
            { "open", "compress.zip", "compress.targz", "rename", "delete", "permissions", "newfolder", "refresh" },
    };
}
