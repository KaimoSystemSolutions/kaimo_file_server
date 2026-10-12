using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ExcelDataReader;

namespace Kaimo_File_Server.Search;



public partial class ContentProvider
{
    // Legacy .xls files store strings in Windows code pages, which .NET only knows
    // once the code-pages provider is registered (required by ExcelDataReader).
    static ContentProvider() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

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
        // Legacy binary Office formats (OLE compound files). Only .xls is read;
        // .doc and .ppt fall through to filename-only indexing.
        if (ext == ".xls")
            return TryExtract(ExtractXlsText, bytes);
        // OpenDocument (ZIP-boxed) formats: text, spreadsheet, presentation.
        if (ext is ".odt" or ".ods" or ".odp")
            return TryExtract(ExtractOdfText, bytes);
        // SVG is XML text, but its markup holds no useful words.
        if (ext == ".svg")
            return string.Empty;

        // No matching extension: sniff the two formats worth extracting by their
        // magic bytes. A ZIP that is not a Word document yields no text.
        if (bytes.AsSpan().StartsWith("%PDF-"u8))
            return TryExtract(ExtractPdfText, bytes);
        if (bytes.AsSpan().StartsWith("PK\u0003\u0004"u8))
            return TryExtract(ExtractDocxText, bytes);

        // Media, archives and executables contain control bytes and fail the text
        // check below, so they are indexed by name only.
        // Try as text
        if (IsLikelyText(bytes))
            return StripEncodedBlobs(Encoding.UTF8.GetString(bytes));

        // Unsupported binary format: index the file by name only. (Returning the detected MIME
        // type here used to make "application", "octet", … match every such file's "content".)
        return string.Empty;
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

    /// <summary>
    /// Drops base64/hex payloads from extracted text: encrypted vaults and backups, embedded
    /// attachments, keys. They pass the text check yet hold no searchable words, and once made
    /// up 95 % of the stored index. A payload is a run of at least 60 base64 characters that
    /// contains a digit; words, identifiers and URLs (which contain '.', ':' or '-') are shorter
    /// or broken up, and long digit-free words are kept.
    /// </summary>
    internal static string StripEncodedBlobs(string text) => EncodedBlobRegex().Replace(text, " ");

    // The lookbehind anchors each attempt at the start of a run, so a scan stays linear.
    [GeneratedRegex(@"(?<![A-Za-z0-9+/=])(?=[A-Za-z0-9+/=]*[0-9])[A-Za-z0-9+/=]{60,}")]
    private static partial Regex EncodedBlobRegex();

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

    // ── Legacy binary Office format (.xls) via ExcelDataReader ──

    private static string ExtractXlsText(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = ExcelReaderFactory.CreateBinaryReader(stream);
        var sb = new StringBuilder();
        do
        {
            while (reader.Read())
            {
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    if (reader.GetValue(i) is not { } value) continue;
                    sb.Append(value);
                    sb.Append(' ');
                }
            }
        } while (reader.NextResult());
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