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
    public DateTime Created { get; set; }
    public DateTime Modified { get; set; }
    public string? Author { get; set; }
    public List<string> Tags { get; set; } = [];

    [JsonIgnore]
    public string? HighlightSnippet { get; set; }
}
