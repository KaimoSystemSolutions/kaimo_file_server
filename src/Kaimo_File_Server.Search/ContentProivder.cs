using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using ExcelDataReader;
using Microsoft.Extensions.Logging;

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

    // No index stores more text than this (local index: 1 M chars, Elasticsearch: 200 K), so
    // extraction stops here instead of building a larger string only to cut it afterwards.
    internal const int MaxExtractedChars = 1_000_000;

    // Upper bound of characters parsed from one XML part of a ZIP-boxed document. A 25 MB file
    // can inflate to gigabytes (ZIP bomb); a part over this cap is treated as hostile and the
    // document is indexed by name only.
    internal const int MaxXmlPartChars = 64 * 1024 * 1024;

    // Formats that never hold extractable text (media, archives, disk images, executables).
    // They are indexed by name without reading a single byte, which matters on shares holding
    // tens of thousands of photos or videos. Formats not listed here are still caught by the
    // head sniff below, at the cost of one small read.
    private static readonly HashSet<string> NoTextExtensions = new(StringComparer.Ordinal)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".heic", ".heif", ".ico", ".psd",
        ".raw", ".cr2", ".cr3", ".nef", ".arw", ".dng", ".orf", ".rw2", ".svg",
        ".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a", ".wma", ".opus",
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".m4v", ".mpg", ".mpeg", ".flv", ".m2ts",
        ".zip", ".7z", ".rar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".tar", ".cab", ".jar", ".apk",
        ".iso", ".img", ".vhd", ".vhdx", ".vmdk", ".qcow2", ".dmg",
        ".exe", ".dll", ".so", ".dylib", ".msi", ".class", ".pdb",
        ".ttf", ".otf", ".woff", ".woff2",
    };

    // Bytes read first to decide whether the rest of the file is worth reading at all.
    private const int SniffBytes = 8 * 1024;

    public static async Task<string> GetContent(Stream fileStream, string fileName = "", ILogger? logger = null)
    {
        // Decide as much as possible before reading: by extension, then by length.
        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (NoTextExtensions.Contains(ext))
            return string.Empty;
        // Oversized: skip content extraction (PDF/docx parsing needs the whole
        // file anyway) and index the filename only. Bounds memory per file.
        if (fileStream.CanSeek && fileStream.Length - fileStream.Position > MaxContentBytes)
            return string.Empty;

        using var memoryStream = new MemoryStream();
        await CopyUpToAsync(fileStream, memoryStream, SniffBytes);
        if (memoryStream.Length == 0)
            return string.Empty;

        var extractor = PickExtractor(ext, memoryStream.GetBuffer().AsSpan(0, (int)memoryStream.Length));
        if (extractor is null)
        {
            // Media, archives and executables contain NUL or control bytes and fail the text
            // check, so they are indexed by name only — without reading past the head.
            // (Returning the detected MIME type here used to make "application", "octet", …
            // match every such file's "content".)
            var head = memoryStream.GetBuffer().AsSpan(0, (int)memoryStream.Length);
            var utf16 = Utf16ByteOrder(head);
            if (utf16 is null && !IsLikelyText(head))
                return string.Empty;

            // Plain text: a UTF-8 or 1252 char is at least one byte, a UTF-16 char two, so this
            // is all the index can use.
            long budget = utf16 is null ? MaxExtractedChars : 2L * MaxExtractedChars + 2;
            await CopyUpToAsync(fileStream, memoryStream, budget - memoryStream.Length);
            var bytes = memoryStream.GetBuffer().AsSpan(0, (int)memoryStream.Length);
            string text = DecodeText(bytes, utf16, truncated: memoryStream.Length == budget);
            return Clean(StripEncodedBlobs(text));
        }

        // Read at most MaxContentBytes + 1 so we can tell "exactly at cap" from
        // "over cap" without loading the whole file (streams that cannot seek).
        await CopyUpToAsync(fileStream, memoryStream, MaxContentBytes + 1 - memoryStream.Length);
        if (memoryStream.Length > MaxContentBytes)
            return string.Empty;

        return Clean(Truncate(ExtractGuarded(extractor, memoryStream.ToArray(), fileName, logger)));
    }

    /// <summary>The UTF-16 variant announced by a byte-order mark (PowerShell 5 output, Notepad "Unicode"), or null.</summary>
    private static Encoding? Utf16ByteOrder(ReadOnlySpan<byte> head)
        => head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) ? Encoding.Unicode
            : head.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]) ? Encoding.BigEndianUnicode
            : null;

    /// <summary>
    /// Decodes plain text: UTF-16 by its byte-order mark, otherwise UTF-8 — or Windows-1252 when
    /// the bytes are no valid UTF-8 (legacy Windows text: "K\xFCndigung" must not become
    /// "K�ndigung"). <paramref name="truncated"/> means the budget may have cut the last
    /// character in half; that alone does not make UTF-8 invalid.
    /// </summary>
    private static string DecodeText(ReadOnlySpan<byte> bytes, Encoding? utf16, bool truncated)
    {
        if (utf16 is not null)
        {
            var body = bytes[2..];
            string wide = utf16.GetString(body[..(body.Length & ~1)]);
            return truncated && wide.Length > 0 && char.IsHighSurrogate(wide[^1]) ? wide[..^1] : wide;
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            bytes = bytes[3..];

        var chars = new char[bytes.Length];
        var status = System.Text.Unicode.Utf8.ToUtf16(
            bytes, chars, out _, out int written, replaceInvalidSequences: false, isFinalBlock: !truncated);
        return status == System.Buffers.OperationStatus.InvalidData
            ? Encoding.GetEncoding(1252).GetString(bytes)
            : new string(chars, 0, written);
    }

    /// <summary>
    /// Final pass over every extracted text: ANSI escape sequences (colored logs) are removed,
    /// remaining control characters become spaces, the private-use characters search uses as
    /// highlight markers are dropped (no snippet can fake a highlight), and the text is
    /// normalized to NFC — macOS writes decomposed "ü", every query is composed "ü".
    /// </summary>
    internal static string Clean(string text)
    {
        text = ControlRegex().Replace(AnsiEscapeRegex().Replace(text, ""), " ");
        if (text.AsSpan().IndexOfAny(SearchDocuments.HighlightOpen, SearchDocuments.HighlightClose) >= 0)
            text = text.Replace(SearchDocuments.HighlightOpen.ToString(), "").Replace(SearchDocuments.HighlightClose.ToString(), "");
        return text.IsNormalized() ? text : text.Normalize();
    }

    // CSI sequences such as "ESC[1;31m", plus any other lone ESC-introduced pair.
    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|[@-_])")]
    private static partial Regex AnsiEscapeRegex();

    // C0 controls except tab, line feed and carriage return; NUL is kept (PDF text, handled downstream).
    [GeneratedRegex(@"[\x01-\x08\x0B\x0C\x0E-\x1F\x7F]")]
    private static partial Regex ControlRegex();

    /// <summary>
    /// The document extractor for a file, or null when it is no supported document format
    /// (plain text or binary, told apart by the caller).
    /// </summary>
    private static Func<byte[], string>? PickExtractor(string ext, ReadOnlySpan<byte> head)
    {
        // Route PDF/docx by file extension first. Magic-byte sniffing sees a docx
        // as a generic ZIP ("application/zip") and would skip it as an archive, so
        // content-based detection alone silently dropped the text of many docx files.
        switch (ext)
        {
            case ".pdf": return ExtractPdfText;
            // OOXML (ZIP-boxed) Office formats.
            case ".docx" or ".docm": return ExtractDocxText;
            case ".xlsx" or ".xlsm": return ExtractXlsxText;
            case ".pptx" or ".pptm": return ExtractPptxText;
            // Legacy binary Office formats (OLE compound files). Only .xls is read;
            // .doc and .ppt fall through to filename-only indexing.
            case ".xls": return ExtractXlsText;
            // OpenDocument (ZIP-boxed) formats: text, spreadsheet, presentation.
            case ".odt" or ".ods" or ".odp": return ExtractOdfText;
        }

        // No matching extension: sniff the two formats worth extracting by their
        // magic bytes. A ZIP that is not a Word document yields no text.
        if (head.StartsWith("%PDF-"u8))
            return ExtractPdfText;
        if (head.StartsWith("PK\u0003\u0004"u8))
            return ExtractDocxText;
        return null;
    }

    /// <summary>Cuts extracted text to <see cref="MaxExtractedChars"/> without splitting a surrogate pair.</summary>
    private static string Truncate(string text)
    {
        if (text.Length <= MaxExtractedChars)
            return text;
        int cut = char.IsHighSurrogate(text[MaxExtractedChars - 1]) ? MaxExtractedChars - 1 : MaxExtractedChars;
        return text[..cut];
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
    /// attachments, keys, JWTs. They pass the text check yet hold no searchable words, and once
    /// made up 95 % of the stored index. Candidates are runs of at least 60 characters over the
    /// base64 and base64url alphabets; one is dropped when it looks random: its letters and
    /// digits switch between upper case, lower case and digit at more than
    /// <see cref="BlobClassChangeRatio"/> of all neighbor pairs (random base64: ~62 %, hex: ~47 %,
    /// camelCase identifiers, compounds such as "…verordnung2024", slugs and snake_case: well
    /// under 40 %). Hex runs up to 128 characters are hashes (SHA-256/512) and stay searchable.
    /// </summary>
    internal static string StripEncodedBlobs(string text)
        => EncodedRunRegex().Replace(text, m => IsEncodedBlob(m.ValueSpan) ? " " : m.Value);

    private const double BlobClassChangeRatio = 0.42;
    private const int MaxHashHexChars = 128;

    private static bool IsEncodedBlob(ReadOnlySpan<char> run)
    {
        if (run.Length <= MaxHashHexChars && !run.ContainsAnyExcept(HexChars))
            return false;

        int pairs = 0, changes = 0, previous = -1;
        foreach (char c in run)
        {
            int cls = char.IsAsciiDigit(c) ? 0 : char.IsAsciiLetterUpper(c) ? 1 : char.IsAsciiLetterLower(c) ? 2 : -1;
            if (cls < 0) continue; // '+', '/', '=', '-', '_' separate nothing here
            if (previous >= 0)
            {
                pairs++;
                if (cls != previous) changes++;
            }
            previous = cls;
        }
        return pairs > 0 && changes > pairs * BlobClassChangeRatio;
    }

    private static readonly System.Buffers.SearchValues<char> HexChars =
        System.Buffers.SearchValues.Create("0123456789abcdefABCDEF");

    // The lookbehind anchors each attempt at the start of a run, so a scan stays linear.
    [GeneratedRegex(@"(?<![A-Za-z0-9+/=_-])[A-Za-z0-9+/=_-]{60,}")]
    private static partial Regex EncodedRunRegex();

    /// <summary>
    /// Text unless the head holds a NUL byte (binary formats virtually always do) or more than a
    /// few other control bytes. Stray controls in real text — ESC in colored logs, form feeds,
    /// backspaces — must not turn a whole file into "binary".
    /// </summary>
    private static bool IsLikelyText(ReadOnlySpan<byte> data)
    {
        var sample = data[..Math.Min(data.Length, 1024)];
        if (sample.Contains((byte)0))
            return false;

        int controls = 0;
        foreach (byte b in sample)
            if (b is < 0x20 and not (0x09 or 0x0A or 0x0D or 0x0C or 0x1B) || b == 0x7F)
                controls++;
        return controls * 20 <= sample.Length; // at most 5 %
    }

    /// <summary>
    /// Directory on persistent storage for crash markers (set by AddSearch); null disables the guard.
    /// </summary>
    internal static string? GuardDirectory { get; set; }

    /// <summary>Markers are content-addressed, so copies of a poison file are caught too.</summary>
    internal static string GuardMarkerPath(string guardDirectory, byte[] bytes)
        => Path.Combine(guardDirectory, Convert.ToHexString(SHA256.HashData(bytes)));

    /// <summary>
    /// Extraction attempts of one content that may die with the process before it is quarantined.
    /// One dead attempt proves nothing (docker stop during a reindex, an OOM caused elsewhere);
    /// dying again on the same content does.
    /// </summary>
    internal const int MaxExtractionAttempts = 2;

    /// <summary>
    /// Runs a document parser under a crash guard. Exceptions are handled by
    /// <see cref="TryExtract"/>, but a parser can also take the whole process down (out of memory,
    /// stack overflow on a malicious PDF). The indexer then restarts on the same file and dies
    /// again — forever. So a marker named after the content, counting the attempts in flight, is
    /// written before parsing and removed afterwards. A marker that already exists was left by a
    /// process that died on this content: the extraction is retried until
    /// <see cref="MaxExtractionAttempts"/> attempts have died, then the file is indexed by name
    /// only. The marker stays (quarantine) until the content changes or an admin deletes it.
    /// If the marker cannot be written the parser still runs, just unguarded.
    /// ponytail: concurrent extractions of identical content (reindex + change log) count as
    /// attempts of each other; a third one in parallel is indexed by name only until the next reindex.
    /// </summary>
    internal static string ExtractGuarded(
        Func<byte[], string> extractor, byte[] bytes, string fileName, ILogger? logger, string? guardDirectory = null)
    {
        guardDirectory ??= GuardDirectory;
        string? marker = guardDirectory is null ? null : GuardMarkerPath(guardDirectory, bytes);

        int deadAttempts = marker is null ? 0 : TryReadGuardMarkerAttempts(marker);
        if (deadAttempts >= MaxExtractionAttempts)
        {
            logger?.LogWarning(
                "Content of {File} is indexed by name only: extracting it crashed the process {Attempts} times. " +
                "Delete {Marker} to retry.", fileName, deadAttempts, marker);
            return string.Empty;
        }

        marker = TryCreateMarker(marker, deadAttempts + 1, fileName);
        try
        {
            return TryExtract(extractor, bytes);
        }
        finally
        {
            if (marker is not null)
                try { File.Delete(marker); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Marker format: the attempt count on the first line, then the file name (for admins).</summary>
    internal static void WriteGuardMarker(string marker, int attempts, string fileName)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, $"{attempts}\n{fileName}");
    }

    /// <summary>Attempts recorded in an existing marker; one when its count is unreadable.</summary>
    internal static int ReadGuardMarkerAttempts(string marker)
        => int.TryParse(File.ReadAllText(marker).Split('\n')[0], out int attempts) ? attempts : 1;

    /// <summary>Attempts recorded in the marker; zero when there is none or it cannot be read.</summary>
    private static int TryReadGuardMarkerAttempts(string marker)
    {
        try
        {
            return File.Exists(marker) ? ReadGuardMarkerAttempts(marker) : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Writes the marker; null if that is not possible.</summary>
    private static string? TryCreateMarker(string? marker, int attempts, string fileName)
    {
        if (marker is null)
            return null;
        try
        {
            WriteGuardMarker(marker, attempts, fileName);
            return marker;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
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
        // Pages are parsed lazily, so stopping at the budget also skips parsing the rest.
        foreach (var page in doc.GetPages())
        {
            if (sb.Length >= MaxExtractedChars) break;
            sb.AppendLine(page.Text);
        }
        return sb.ToString();
    }

    private const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static string ExtractDocxText(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml");
        if (entry == null) return string.Empty;

        var sb = new StringBuilder();
        AppendXmlText(entry, sb, r => r.LocalName == "t" && r.NamespaceURI == WordNamespace, separator: "");
        return sb.ToString();
    }

    /// <summary>
    /// Streams one XML part of a ZIP-boxed document and appends its text until the text budget
    /// is reached. With <paramref name="isTextElement"/> only text inside matching elements is
    /// taken (followed by <paramref name="separator"/>), otherwise every text node is.
    /// No DOM is built and parsing is capped at <see cref="MaxXmlPartChars"/> (the reader
    /// throws beyond it), so an inflating part (ZIP bomb) cannot exhaust memory.
    /// </summary>
    private static void AppendXmlText(
        ZipArchiveEntry entry, StringBuilder sb, Func<XmlReader, bool>? isTextElement, string separator)
    {
        using var reader = XmlReader.Create(entry.Open(), new XmlReaderSettings
        {
            MaxCharactersInDocument = MaxXmlPartChars,
            DtdProcessing = DtdProcessing.Ignore,
            CloseInput = true
        });

        bool inText = isTextElement is null;
        while (sb.Length < MaxExtractedChars && reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element when isTextElement is not null && !reader.IsEmptyElement:
                    inText = isTextElement(reader);
                    break;
                case XmlNodeType.EndElement when isTextElement is not null && inText:
                    inText = false;
                    sb.Append(separator);
                    break;
                case XmlNodeType.Text or XmlNodeType.CDATA when inText:
                    sb.Append(reader.Value);
                    if (isTextElement is null) sb.Append(separator);
                    break;
                // Whitespace a text element preserves on purpose (xml:space="preserve").
                case XmlNodeType.SignificantWhitespace when inText && isTextElement is not null:
                    sb.Append(reader.Value);
                    break;
            }
        }
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
            if (sb.Length >= MaxExtractedChars) break;
            if (!partFilter(entry.FullName)) continue;

            AppendXmlText(entry, sb, r => r.LocalName == "t", separator: " ");
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
            while (sb.Length < MaxExtractedChars && reader.Read())
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

        var sb = new StringBuilder();
        AppendXmlText(entry, sb, isTextElement: null, separator: " ");
        return sb.ToString().TrimEnd();
    }

}