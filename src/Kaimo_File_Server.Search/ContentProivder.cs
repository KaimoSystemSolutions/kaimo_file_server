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

    // Hard cap on how much of a file is ever pulled into memory for indexing.
    // Reindex processes files sequentially, but a single unbounded read of a huge
    // file (video, ISO, disk image) allocates its full size twice (MemoryStream +
    // ToArray) and OOM-kills the process. The first few MB are more than enough
    // for full-text search; oversized files fall back to filename-only indexing.
    // ponytail: fixed 25 MB cap, make it a setting if someone needs deeper content.
    private const long MaxContentBytes = 25L * 1024 * 1024;

    public static async Task<string> GetContent(Stream fileStream, string fileName = "")
    {
        using var memoryStream = new MemoryStream();
        // Read at most MaxContentBytes + 1 so we can tell "exactly at cap" from
        // "over cap" without loading the whole file.
        await CopyUpToAsync(fileStream, memoryStream, MaxContentBytes + 1);
        byte[] bytes = memoryStream.ToArray();

        if (bytes.Length == 0)
            return string.Empty;

        // Oversized: skip content extraction (PDF/docx parsing needs the whole
        // file anyway) and index the filename only. Bounds memory per file.
        if (bytes.Length > MaxContentBytes)
            return string.Empty;

        // Route PDF/docx by file extension first. Magic-byte sniffing sees a docx
        // as a generic ZIP ("application/zip") and would skip it as an archive, so
        // content-based detection alone silently dropped the text of many docx files.
        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext == ".pdf")
            return TryExtract(ExtractPdfText, bytes);
        // OOXML (ZIP-boxed) Office formats.
        if (ext is ".docx" or ".docm")
            return TryExtract(ExtractDocxText, bytes);
        if (ext is ".xlsx" or ".xlsm")
            return TryExtract(ExtractXlsxText, bytes);
        if (ext is ".pptx" or ".pptm")
            return TryExtract(ExtractPptxText, bytes);
        // Legacy binary Office formats (OLE compound files). NPOI's .NET build only
        // ships HSSF, so .xls works; .doc (HWPF) and .ppt (HSLF) are not supported
        // by the library and fall through to filename-only indexing.
        if (ext == ".xls")
            return TryExtract(ExtractXlsText, bytes);
        // OpenDocument (ZIP-boxed) formats: text, spreadsheet, presentation.
        if (ext is ".odt" or ".ods" or ".odp")
            return TryExtract(ExtractOdfText, bytes);

        var results = Inspector.Inspect(bytes);
        var mimeType = results.ByMimeType().FirstOrDefault()?.MimeType.ToLower();

        if (mimeType != null)
        {
            if (SkipMimeTypes.Contains(mimeType))
                return string.Empty;

            if (PdfMimeTypes.Contains(mimeType))
                return TryExtract(ExtractPdfText, bytes);

            if (DocxMimeTypes.Contains(mimeType))
                return TryExtract(ExtractDocxText, bytes);
        }

        // No known binary signature matched — try as text
        if (IsLikelyText(bytes))
            return Encoding.UTF8.GetString(bytes);

        return mimeType is not null ?  mimeType! : string.Empty;
    }

    /// <summary>
    /// Copies at most <paramref name="maxBytes"/> from source to destination.
    /// Unlike <see cref="Stream.CopyToAsync(Stream)"/> this never reads the whole
    /// file, so a huge input can't blow up memory during indexing.
    /// </summary>
    private static async Task CopyUpToAsync(Stream source, Stream destination, long maxBytes)
    {
        var buffer = new byte[81920];
        long total = 0;
        while (total < maxBytes)
        {
            int want = (int)Math.Min(buffer.Length, maxBytes - total);
            int read = await source.ReadAsync(buffer.AsMemory(0, want));
            if (read == 0)
                break;
            await destination.WriteAsync(buffer.AsMemory(0, read));
            total += read;
        }
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

    /// <summary>
    /// Runs an extractor, returning empty on failure. A corrupt, encrypted or
    /// otherwise unparseable PDF/docx then still gets indexed by filename instead
    /// of throwing and dropping the whole document from the index.
    /// </summary>
    private static string TryExtract(Func<byte[], string> extractor, byte[] bytes)
    {
        try
        {
            return extractor(bytes);
        }
        catch
        {
            // ponytail: swallow parse errors, filename-only fallback. Add logging
            // (needs an ILogger passed in) if silent misses become a problem.
            return string.Empty;
        }
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

    // xlsx and pptx are OOXML packages just like docx (a ZIP of XML parts). The
    // textual content sits in <t> elements — sharedStrings for a workbook, the
    // slide parts for a presentation — regardless of the XML namespace, so both
    // reuse the same local-name "t" scan restricted to the relevant parts.
    private static string ExtractXlsxText(byte[] bytes)
        => ExtractOoxmlText(bytes, name => name == "xl/sharedStrings.xml");

    private static string ExtractPptxText(byte[] bytes)
        => ExtractOoxmlText(bytes, name =>
            name.StartsWith("ppt/slides/slide", StringComparison.Ordinal)
            && name.EndsWith(".xml", StringComparison.Ordinal));

    private static string ExtractOoxmlText(byte[] bytes, Func<string, bool> partFilter)
    {
        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        var sb = new StringBuilder();
        foreach (var entry in zip.Entries)
        {
            if (!partFilter(entry.FullName)) continue;

            using var entryStream = entry.Open();
            var doc = XDocument.Load(entryStream);
            foreach (var t in doc.Descendants().Where(e => e.Name.LocalName == "t"))
            {
                sb.Append(t.Value);
                sb.Append(' ');
            }
        }
        return sb.ToString();
    }

    // ── Legacy binary Office format (.xls) via NPOI/HSSF ──

    private static string ExtractXlsText(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var workbook = new NPOI.HSSF.UserModel.HSSFWorkbook(stream);
        var sb = new StringBuilder();
        for (int i = 0; i < workbook.NumberOfSheets; i++)
        {
            var sheet = workbook.GetSheetAt(i);
            foreach (NPOI.SS.UserModel.IRow row in sheet)
            {
                foreach (NPOI.SS.UserModel.ICell cell in row)
                {
                    sb.Append(cell.ToString());
                    sb.Append(' ');
                }
            }
        }
        return sb.ToString();
    }

    // ── OpenDocument formats (ZIP-boxed) ──

    // ODF stores the document body as marked-up XML in content.xml; unlike OOXML
    // the text is the elements' own content, not <t> nodes. Concatenating every
    // text node yields the searchable text across odt/ods/odp alike.
    private static string ExtractOdfText(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = zip.GetEntry("content.xml");
        if (entry == null) return string.Empty;

        using var entryStream = entry.Open();
        var doc = XDocument.Load(entryStream);
        var texts = doc.DescendantNodes().OfType<XText>().Select(t => t.Value);
        return string.Join(" ", texts);
    }

}