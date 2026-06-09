namespace Kaimo_File_Server.Web.DynamicHelpers;

public class ContextMenuBuilder
{
    private readonly List<ContextMenuItem> _items = new();

    public ContextMenuBuilder Add(string label, string icon, Func<Task> action,
        bool isDanger = false, SvgOptions? iconOptions = null)
    {
        _items.Add(new ContextMenuItem(label, icon, action, isDanger)
        {
            IconOptions = iconOptions ?? new SvgOptions(Width: 14, Height: 14)
        });
        return this;
    }

    public ContextMenuBuilder AddSeparator()
    {
        _items.Add(ContextMenuItem.Separator());
        return this;
    }

    public ContextMenuBuilder AddHeader(string label)
    {
        _items.Add(ContextMenuItem.Header(label));
        return this;
    }

    public ContextMenuBuilder AddIf(bool condition, string label, string icon,
        Func<Task> action, bool isDanger = false, SvgOptions? iconOptions = null)
    {
        if (condition) Add(label, icon, action, isDanger, iconOptions);
        return this;
    }

    public List<ContextMenuItem> Build() => _items;
}