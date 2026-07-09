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

    [JsonIgnore]
    public string? HighlightSnippet { get; set; }
}
