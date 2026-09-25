using System.Text.Json.Serialization;

namespace Kaimo_File_Server.Search;

public class FileDocument
{
    public required string Id { get; set; }
    public required string FileName { get; set; }
    public required string ShareName { get; set; }
    public required string AbsolutePath { get; set; }
    public required string SharePath { get; set; }
    
    public string Content { get; set; } = string.Empty;
    public required string FileType { get; set; }
    public long FileSizeBytes { get; set; }

    /// <summary>
    /// True when this hit is a directory rather than a file. Directories are
    /// indexed by name only (no content) so they show up in search too. The UI
    /// uses this to pick a folder icon and to navigate INTO the folder instead of
    /// selecting a file, and the ACL filter uses it to resolve directory ACLs.
    /// </summary>
    public bool IsDirectory { get; set; }
    public DateTime Created { get; set; }
    public DateTime Modified { get; set; }
    public string? Author { get; set; }
    public List<string> Tags { get; set; } = [];

    /// <summary>Search hit excerpt with the matched terms wrapped in <c>&lt;mark&gt;</c> tags.</summary>
    /// <remarks>
    /// Raw, unencoded text: file names and content are user-controlled. Consumers must
    /// never render it as HTML — split it on the markers and emit encoded text instead
    /// (see <c>Kaimo_File_Server.Web.Components.Search.SearchSnippet</c>).
    /// </remarks>
    [JsonIgnore]
    public string? HighlightSnippet { get; set; }
}
