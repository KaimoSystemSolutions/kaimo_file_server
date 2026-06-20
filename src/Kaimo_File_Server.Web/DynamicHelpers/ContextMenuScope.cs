using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Web.Helpers;

namespace Kaimo_File_Server.Web.DynamicHelpers;

/// <summary>
/// The "context" a right-click happens in. Each scope has its own configurable
/// context-menu layout (see <see cref="ContextMenuConfig"/>). The scope is
/// resolved from the current selection via <see cref="ResolveScope"/>.
/// </summary>
public enum ContextMenuScope
{
    /// <summary>Right-click on empty area / no selection.</summary>
    Background,
    /// <summary>A single directory.</summary>
    Folder,
    /// <summary>A single archive file (.zip, .tar, …).</summary>
    Archive,
    /// <summary>A single image file.</summary>
    Image,
    /// <summary>A single video file.</summary>
    Video,
    /// <summary>A single audio file.</summary>
    Audio,
    /// <summary>A single document (pdf, docx, text/source, …).</summary>
    Document,
    /// <summary>A single file that fits none of the other categories.</summary>
    OtherFile,
    /// <summary>More than one item selected.</summary>
    MultiSelection,
}

public static class ContextMenuScopeResolver
{
    /// <summary>
    /// Derives the menu scope from the current selection, using the existing
    /// <see cref="FileHelper"/> categorisation (directory / archive / preview kind).
    /// </summary>
    public static ContextMenuScope ResolveScope(IReadOnlyCollection<FileMetadata> selected)
    {
        if (selected.Count == 0) return ContextMenuScope.Background;
        if (selected.Count > 1) return ContextMenuScope.MultiSelection;

        var target = selected.First();
        if (target.IsDirectory) return ContextMenuScope.Folder;
        if (FileHelper.IsArchive(target.Name)) return ContextMenuScope.Archive;

        return FileHelper.GetPreviewKind(target.Name) switch
        {
            PreviewKind.Image => ContextMenuScope.Image,
            PreviewKind.Video => ContextMenuScope.Video,
            PreviewKind.Audio => ContextMenuScope.Audio,
            PreviewKind.Pdf or PreviewKind.Docx or PreviewKind.Text => ContextMenuScope.Document,
            _ => ContextMenuScope.OtherFile,
        };
    }
}
