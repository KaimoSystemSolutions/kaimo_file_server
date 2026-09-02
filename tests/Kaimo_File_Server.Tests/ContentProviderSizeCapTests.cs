using System.Text;
using Kaimo_File_Server.Search;
using Xunit;

namespace Kaimo_File_Server.Tests;

public class ContentProviderSizeCapTests
{
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
