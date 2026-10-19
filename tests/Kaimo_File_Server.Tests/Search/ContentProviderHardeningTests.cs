using System.IO.Compression;
using System.Text;
using Kaimo_File_Server.Search;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Content extraction must stay cheap for files that hold no searchable text (media, archives,
/// unknown binaries) and bounded for hostile ones (ZIP bombs, huge text): it decides from the
/// extension, the stream length and a small head sniff before reading more, and never extracts
/// more text than any index stores.
/// </summary>
public sealed class ContentProviderHardeningTests
{
    [Theory]
    [InlineData("movie.mp4")]
    [InlineData("photo.JPG")]
    [InlineData("backup.zip")]
    [InlineData("disk.iso")]
    [InlineData("setup.exe")]
    public async Task GetContent_KnownBinaryExtension_DoesNotReadTheFile(string fileName)
    {
        using var stream = new UnreadableStream(length: 1024);

        Assert.Equal(string.Empty, await ContentProvider.GetContent(stream, fileName));
    }

    [Fact]
    public async Task GetContent_OversizedSeekableStream_DoesNotReadTheFile()
    {
        using var stream = new UnreadableStream(length: 30L * 1024 * 1024);

        Assert.Equal(string.Empty, await ContentProvider.GetContent(stream, "huge.log"));
    }

    [Fact]
    public async Task GetContent_UnknownBinary_ReadsOnlyTheHead()
    {
        using var stream = new CountingStream(new byte[10 * 1024 * 1024]); // all zero bytes

        Assert.Equal(string.Empty, await ContentProvider.GetContent(stream, "data.bin"));
        Assert.True(stream.BytesRead <= 64 * 1024, $"read {stream.BytesRead} bytes");
    }

    [Fact]
    public async Task GetContent_LargeText_ReadsAndReturnsAtMostTheTextBudget()
    {
        var text = string.Concat(Enumerable.Repeat("hello world ", 300_000)); // 3.6 M chars
        using var stream = new CountingStream(Encoding.UTF8.GetBytes(text));

        var content = await ContentProvider.GetContent(stream, "big.txt");

        Assert.StartsWith("hello world", content);
        Assert.True(content.Length <= ContentProvider.MaxExtractedChars, $"content {content.Length} chars");
        Assert.True(stream.BytesRead <= ContentProvider.MaxExtractedChars + 64 * 1024, $"read {stream.BytesRead} bytes");
    }

    [Fact]
    public async Task GetContent_LargeSpreadsheet_StopsAtTheTextBudget()
    {
        var cells = string.Concat(Enumerable.Repeat("<si><t>quarterly figures</t></si>", 100_000)); // 1.7 M text chars
        using var stream = Zip("xl/sharedStrings.xml", $"<sst xmlns=\"x\">{cells}</sst>");

        var content = await ContentProvider.GetContent(stream, "book.xlsx");

        Assert.Contains("quarterly figures", content);
        Assert.True(content.Length <= ContentProvider.MaxExtractedChars, $"content {content.Length} chars");
    }

    [Fact]
    public async Task GetContent_ZipBomb_IsIndexedByNameOnly()
    {
        // A few KB on disk, but the XML part inflates beyond the per-part cap.
        const string w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        using var stream = Zip("word/document.xml",
            $"<w:document xmlns:w=\"{w}\"><w:body><w:t>secret</w:t><w:p>",
            filler: ContentProvider.MaxXmlPartChars + 1,
            "</w:p></w:body></w:document>");

        Assert.Equal(string.Empty, await ContentProvider.GetContent(stream, "bomb.docx"));
    }

    [Fact]
    public async Task GetContent_Docx_JoinsAdjacentRunsAndIgnoresOtherNamespaces()
    {
        const string w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        using var stream = Zip("word/document.xml",
            $"<w:document xmlns:w=\"{w}\" xmlns:a=\"drawing\"><w:body><w:p>" +
            "<w:r><w:t>Hel</w:t></w:r><w:r><w:t>lo</w:t><w:t xml:space=\"preserve\"> </w:t><w:t>world</w:t></w:r>" +
            "<a:t>shape</a:t></w:p></w:body></w:document>");

        Assert.Equal("Hello world", await ContentProvider.GetContent(stream, "letter.docx"));
    }

    private static MemoryStream Zip(string partName, string xml) => Zip(partName, xml, 0, "");

    /// <summary>Builds a one-part ZIP; <paramref name="filler"/> spaces go between head and tail.</summary>
    private static MemoryStream Zip(string partName, string head, long filler, string tail)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        using (var entry = new StreamWriter(zip.CreateEntry(partName, CompressionLevel.Fastest).Open()))
        {
            entry.Write(head);
            var chunk = new string(' ', 1024 * 1024);
            for (long left = filler; left > 0; left -= chunk.Length)
                entry.Write(chunk.AsSpan(0, (int)Math.Min(chunk.Length, left)));
            entry.Write(tail);
        }
        stream.Position = 0;
        return stream;
    }

    /// <summary>A seekable stream of the given length that fails the test if anything reads it.</summary>
    private sealed class UnreadableStream(long length) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("read");
        public override int Read(Span<byte> buffer) => throw new InvalidOperationException("read");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => throw new InvalidOperationException("read");
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => throw new InvalidOperationException("read");
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// A memory stream that counts how many bytes were read from it. MemoryStream's async reads
    /// complete through these synchronous overloads, so counting here covers them too.
    /// </summary>
    internal sealed class CountingStream(byte[] data) : MemoryStream(data)
    {
        public long BytesRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count) => Count(base.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer) => Count(base.Read(buffer));

        private int Count(int read)
        {
            BytesRead += read;
            return read;
        }
    }
}
