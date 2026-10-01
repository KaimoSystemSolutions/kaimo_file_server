using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Search;
using Kaimo_File_Server.Web.Components.Search;

namespace Kaimo_File_Server.Web.Controllers.Api;

/// <summary>A search request. Sent as a POST body so search terms never land in URL/proxy logs.</summary>
/// <param name="Q">The search text (2–100 characters, no control characters).</param>
/// <param name="ShareId">Optional: restrict the search to one share.</param>
/// <param name="Path">Optional share-relative folder (requires <paramref name="ShareId"/>): that folder and below.</param>
/// <param name="Limit">Maximum number of hits (1–50, default 25).</param>
public sealed record SearchRequestDto(string? Q, Guid? ShareId = null, string? Path = null, int? Limit = null);

/// <summary>One piece of a highlight snippet; <c>Highlighted</c> marks the matched text.</summary>
public sealed record SnippetSegmentDto(string Text, bool Highlighted);

/// <summary>
/// A search hit the caller may read. Deliberately omits the server's absolute path,
/// the internal document id and the indexed full text. <c>ShareId</c> + <c>Path</c>
/// address the item in <c>api/v1/browse</c>. Carries no modification time: the index
/// only knows when an item was indexed, so clients read timestamps from browse.
/// </summary>
public sealed record SearchHitDto(
    Guid ShareId,
    string ShareName,
    string Path,
    string Name,
    bool IsDirectory,
    string FileType,
    long Size,
    IReadOnlyList<SnippetSegmentDto> Snippet)
{
    public static SearchHitDto From(FileDocument d, Guid shareId, string shareName) => new(
        shareId,
        shareName,
        ShareRelativePath.Normalize(d.SharePath),
        d.FileName,
        d.IsDirectory,
        d.FileType,
        d.FileSizeBytes,
        // Structured segments instead of raw "<mark>" markup: clients never parse or
        // render HTML, so user-controlled names/content cannot inject anything.
        SearchSnippet.Split(d.HighlightSnippet)
            .Select(s => new SnippetSegmentDto(s.Text, s.Highlighted))
            .ToList());
}
