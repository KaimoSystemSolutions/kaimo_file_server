namespace Kaimo_File_Server.Web.DynamicHelpers;

public record ContextMenuItem(
    string Label,
    string Icon,
    Func<Task> Action,
    bool IsDanger = false,
    bool IsSeparator = false,
    bool IsHeader = false
)
{
    public SvgOptions IconOptions { get; init; } = new(Width: 14, Height: 14);

    // Factory helpers
    public static ContextMenuItem Separator() => new("", "", () => Task.CompletedTask, IsSeparator: true);
    public static ContextMenuItem Header(string label) => new(label, "", () => Task.CompletedTask, IsHeader: true);
}