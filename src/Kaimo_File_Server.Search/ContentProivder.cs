using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using MimeDetective;
using MimeDetective.Definitions;
using MimeDetective.Definitions.Licensing;
using NPOI.OpenXmlFormats.Wordprocessing;

namespace Kaimo_File_Server.Search;



public class ContentProvider
{
    private static readonly IContentInspector Inspector = new ContentInspectorBuilder()
    {
        Definitions = new ExhaustiveBuilder()
        {
            UsageType = UsageType.PersonalNonCommercial
        }.Build()
    }.Build();

    private static readonly HashSet<string> SkipMimeTypes = new()
    {
        // Images
        "image/png", "image/jpeg", "image/gif", "image/bmp",
        "image/webp", "image/tiff", "image/svg+xml", "image/x-icon",

        // Audio
        "audio/mpeg", "audio/wav", "audio/ogg", "audio/flac",
        "audio/aac", "audio/webm",

        // Video
        "video/mp4", "video/mpeg", "video/webm", "video/x-msvideo",
        "video/quicktime", "video/x-matroska",

        // Archives
        "application/zip", "application/gzip", "application/x-rar-compressed",
        "application/x-7z-compressed", "application/x-tar",

        // Executables
        "application/x-executable", "application/x-dosexec",
        "application/vnd.microsoft.portable-executable",
        "application/x-mach-binary",
    };

    private static readonly HashSet<string> PdfMimeTypes = new()
    {
        "application/pdf",
    };
    private static readonly HashSet<string> DocxMimeTypes = new()
    {
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
    };

    public static async Task<string> GetContent(Stream fileStream)
    {
        using var memoryStream = new MemoryStream();
        await fileStream.CopyToAsync(memoryStream);
        byte[] bytes = memoryStream.ToArray();

        if (bytes.Length == 0)
            return string.Empty;

        var results = Inspector.Inspect(bytes);
        var mimeType = results.ByMimeType().FirstOrDefault()?.MimeType.ToLower();

        if (mimeType != null)
        {
            if (SkipMimeTypes.Contains(mimeType))
                return string.Empty;

            if (PdfMimeTypes.Contains(mimeType))
                return ExtractPdfText(bytes);

            if (DocxMimeTypes.Contains(mimeType))
                return ExtractDocxText(bytes);
        }

        // No known binary signature matched — try as text
        if (IsLikelyText(bytes))
            return Encoding.UTF8.GetString(bytes);

        return mimeType is not null ?  mimeType! : string.Empty;
    }

    private static bool IsLikelyText(byte[] data)
    {
        int checkLength = Math.Min(data.Length, 1024);
        int start = (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) ? 3 : 0;

        for (int i = start; i < checkLength; i++)
        {
            byte b = data[i];
            if (b == 0x09 || b == 0x0A || b == 0x0D) continue;
            if (b >= 0x20 && b <= 0x7E) continue;
            if (b >= 0x80) continue;
            return false;
        }
        return true;
    }

    private static string ExtractPdfText(byte[] bytes)
    {
        using var doc = UglyToad.PdfPig.PdfDocument.Open(bytes);
        var sb = new StringBuilder();
        foreach (var page in doc.GetPages())
            sb.AppendLine(page.Text);
        return sb.ToString();
    }
    
    private static string ExtractDocxText(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml");
        if (entry == null) return string.Empty;

        using var entryStream = entry.Open();
        var doc = XDocument.Load(entryStream);
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

        var texts = doc.Descendants(w + "t").Select(t => t.Value);
        return string.Join("", texts);
    }
    
}