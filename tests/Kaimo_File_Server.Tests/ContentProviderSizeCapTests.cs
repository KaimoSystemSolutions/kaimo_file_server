using System.IO.Compression;
using System.Text;
using Kaimo_File_Server.Search;
using Xunit;

namespace Kaimo_File_Server.Tests;

public class ContentProviderSizeCapTests
{
    // OOXML formats (docx/xlsx/pptx) are ZIP containers, so magic-byte sniffing
    // reports them as "application/zip" and used to drop their text. Routing by
    // extension must extract the <t> nodes from the relevant package parts.
    [Theory]
    [InlineData("book.xlsx", "xl/sharedStrings.xml",
        "<sst xmlns=\"x\"><si><t>invoice total</t></si></sst>", "invoice")]
    [InlineData("deck.pptx", "ppt/slides/slide1.xml",
        "<p><a:t xmlns:a=\"a\">quarterly roadmap</a:t></p>", "roadmap")]
    public async Task GetContent_OoxmlPackage_ExtractsText(
        string fileName, string partName, string partXml, string expected)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = new StreamWriter(zip.CreateEntry(partName).Open());
            entry.Write(partXml);
        }
        stream.Position = 0;

        var content = await ContentProvider.GetContent(stream, fileName);

        Assert.Contains(expected, content);
    }

    // OpenDocument formats are ZIP-boxed too, but the body text is the elements'
    // own content in content.xml (not <t> nodes), so it takes a separate path.
    [Theory]
    [InlineData("notes.odt")]
    [InlineData("sheet.ods")]
    [InlineData("slides.odp")]
    public async Task GetContent_OpenDocument_ExtractsText(string fileName)
    {
        const string contentXml =
            "<office:document-content xmlns:office=\"o\" xmlns:text=\"t\">" +
            "<office:body><text:p>budget forecast</text:p></office:body>" +
            "</office:document-content>";

        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = new StreamWriter(zip.CreateEntry("content.xml").Open());
            entry.Write(contentXml);
        }
        stream.Position = 0;

        var content = await ContentProvider.GetContent(stream, fileName);

        Assert.Contains("forecast", content);
    }

    // Legacy .xls (HSSF) is the one binary Office format NPOI's .NET build supports.
    [Fact]
    public async Task GetContent_LegacyXls_ExtractsCellText()
    {
        using var stream = new MemoryStream();
        using (var workbook = new NPOI.HSSF.UserModel.HSSFWorkbook())
        {
            var cell = workbook.CreateSheet("Sheet1").CreateRow(0).CreateCell(0);
            cell.SetCellValue("annual report");
            workbook.Write(stream, leaveOpen: true);
        }
        stream.Position = 0;

        var content = await ContentProvider.GetContent(stream, "old.xls");

        Assert.Contains("annual report", content);
    }

    [Fact]
    public async Task GetContent_SmallText_ReturnsContent()
    {
        var bytes = Encoding.UTF8.GetBytes("hello reindex world");
        using var stream = new MemoryStream(bytes);

        var content = await ContentProvider.GetContent(stream);

        Assert.Contains("reindex", content);
    }

    [Fact]
    public async Task GetContent_OverSizedFile_ReturnsEmpty_DoesNotLoadWholeFile()
    {
        // 30 MB > the 25 MB cap: must be skipped (filename-only), not indexed,
        // and must not OOM by reading the whole thing.
        const int size = 30 * 1024 * 1024;
        var buffer = new byte[size];
        Array.Fill(buffer, (byte)'A');
        using var stream = new MemoryStream(buffer);

        var content = await ContentProvider.GetContent(stream);

        Assert.Equal(string.Empty, content);
    }
}
