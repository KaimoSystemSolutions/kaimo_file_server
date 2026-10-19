using System.Text;
using Kaimo_File_Server.Search;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Plain-text detection and decoding: real-world text with stray control characters, UTF-16 and
/// legacy Windows-1252 must be indexed readably, while binary data still yields no content.
/// Also covers the encoded-blob filter, which must not swallow hashes and long real words.
/// </summary>
public sealed class ContentProviderTextDetectionTests
{
    private static Task<string> Extract(byte[] bytes, string name = "a.txt")
        => ContentProvider.GetContent(new MemoryStream(bytes), name);

    [Fact]
    public async Task AnsiColoredLog_IsText_AndEscapeSequencesAreRemoved()
    {
        var bytes = Encoding.UTF8.GetBytes("\u001b[32mINFO\u001b[0m server started\n\u001b[31mERROR\u001b[0m disk full\n");

        var text = await Extract(bytes, "app.log");

        Assert.Contains("INFO", text);
        Assert.Contains("disk full", text);
        Assert.DoesNotContain('\u001b', text);
        Assert.DoesNotContain("[32m", text);
    }

    [Fact]
    public async Task FormFeed_DoesNotMakeAFileBinary()
    {
        var text = await Extract(Encoding.ASCII.GetBytes("page one\fpage two"));

        Assert.Contains("page one", text);
        Assert.Contains("page two", text);
        Assert.DoesNotContain('\f', text);
    }

    [Fact]
    public async Task NulBytes_StillMeanBinary()
    {
        var bytes = Encoding.ASCII.GetBytes("header\0\0\0\u0001\u0002 payload");

        Assert.Equal(string.Empty, await Extract(bytes, "data.bin"));
    }

    [Theory]
    [InlineData("utf-16")]    // little endian, what PowerShell 5 writes
    [InlineData("utf-16BE")]
    public async Task Utf16WithBom_IsDecoded(string encodingName)
    {
        var encoding = Encoding.GetEncoding(encodingName);
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes("Kündigung zum 31.12.")).ToArray();

        Assert.Equal("Kündigung zum 31.12.", await Extract(bytes, "out.txt"));
    }

    [Fact]
    public async Task Windows1252_IsDecoded()
    {
        var bytes = CodePagesEncoding(1252).GetBytes("Kündigung für Müller – 5 €");

        Assert.Equal("Kündigung für Müller – 5 €", await Extract(bytes));
    }

    [Fact]
    public async Task Utf8_IsNotMistakenForWindows1252()
    {
        var bytes = Encoding.UTF8.GetBytes("Kündigung für Müller – 5 €");

        Assert.Equal("Kündigung für Müller – 5 €", await Extract(bytes));
    }

    [Fact]
    public async Task Utf8CutInsideACharacterAtTheBudget_IsStillUtf8()
    {
        // The budget cuts the last 'ü' (2 bytes) in half; that must not flip the file to 1252.
        var text = new string('a', ContentProvider.MaxExtractedChars - 1) + "ü";
        var result = await Extract(Encoding.UTF8.GetBytes(text));

        Assert.Equal(ContentProvider.MaxExtractedChars - 1, result.Length);
        Assert.DoesNotContain('Ã', result);
    }

    [Fact]
    public async Task DecomposedText_IsNormalizedToComposedForm()
    {
        // macOS writes NFD; the index and every query use NFC.
        var bytes = Encoding.UTF8.GetBytes("Kündigung");

        Assert.Equal("Kündigung", await Extract(bytes));
    }

    [Fact]
    public async Task HighlightMarkerCharacters_AreRemovedFromContent()
    {
        var bytes = Encoding.UTF8.GetBytes("abc");

        Assert.Equal("abc", await Extract(bytes));
    }

    // ── encoded-blob filter ──

    [Theory]
    // SHA-256 and SHA-512 in hex: people search for these.
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("CF83E1357EEFB8BDF1542850D66D8007D620E4050B5715DC83F4A921D36CE9CE47D0D13C5D85F2B0FF8318D2877EEC2F63B931BD47417A81A538327AF927DA3E")]
    // Long camelCase identifier with a digit.
    [InlineData("getUserProfileByIdV2HandlerForAdministratorsWithExtendedPermissions")]
    // Compound word with a year.
    [InlineData("Bundesdatenschutzgrundverordnungsdurchfuehrungsbestimmungen2024")]
    public void StripEncodedBlobs_KeepsHashesIdentifiersAndCompounds(string word)
    {
        Assert.Contains(word, ContentProvider.StripEncodedBlobs($"see {word} here"));
    }

    [Fact]
    public void StripEncodedBlobs_DropsLongHexDumps()
    {
        var dump = Convert.ToHexString(Enumerable.Range(0, 200).Select(i => (byte)(i * 73 + 5)).ToArray());

        var result = ContentProvider.StripEncodedBlobs($"dump: {dump} end");

        Assert.DoesNotContain(dump[..40], result);
        Assert.Contains("dump:", result);
        Assert.Contains("end", result);
    }

    [Fact]
    public void StripEncodedBlobs_DropsRandomBase64EvenWithoutDigits()
    {
        var random = new Random(7);
        var blob = new string(Enumerable.Range(0, 120)
            .Select(_ => "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"[random.Next(52)]).ToArray());

        Assert.DoesNotContain(blob[..30], ContentProvider.StripEncodedBlobs($"x {blob} y"));
    }

    private static Encoding CodePagesEncoding(int codePage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(codePage);
    }
}
