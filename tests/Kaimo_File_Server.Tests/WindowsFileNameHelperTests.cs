using System.Text;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Names created by non-Windows clients (Linux/macOS SMB, REST, cloud syncs) must end up
/// usable on Windows. The helper decides what is valid and how an invalid name is renamed.
/// </summary>
public class WindowsFileNameHelperTests
{
    [Theory]
    [InlineData("Report (2).docx")]
    [InlineData("CONSOLE.txt")]
    [InlineData("COM10")]
    [InlineData("report..final")]
    [InlineData(".hidden")]
    public void IsValid_AcceptsWindowsSafeNames(string name)
        => Assert.True(WindowsFileNameHelper.IsValid(name));

    [Theory]
    [InlineData("a:b")]
    [InlineData("x?.txt")]
    [InlineData("a\tb")]
    [InlineData("foo.")]
    [InlineData("foo ")]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("CON.tar.gz")]
    [InlineData("lpt1.log")]
    [InlineData("")]
    public void IsValid_RejectsWindowsInvalidNames(string name)
        => Assert.False(WindowsFileNameHelper.IsValid(name));

    [Fact]
    public void IsValid_CountsLengthInUtf8Bytes()
    {
        Assert.True(WindowsFileNameHelper.IsValid(new string('ä', 127)));  // 254 bytes
        Assert.False(WindowsFileNameHelper.IsValid(new string('ä', 128))); // 256 bytes
    }

    [Theory]
    [InlineData("a:b", "a_b")]
    [InlineData("a<>b", "a_b")]
    [InlineData("x?.txt", "x_.txt")]
    [InlineData("a\tb", "a_b")]
    [InlineData("foo.", "foo")]
    [InlineData("foo. . ", "foo")]
    [InlineData("CON", "CON_")]
    [InlineData("con.txt", "con_.txt")]
    [InlineData("CON.tar.gz", "CON_.tar.gz")]
    [InlineData("COM1", "COM1_")]
    [InlineData("", "_")]
    [InlineData("...", "_")]
    [InlineData("Report (2).docx", "Report (2).docx")]
    public void Sanitize_ProducesValidName(string name, string expected)
    {
        var result = WindowsFileNameHelper.Sanitize(name);
        Assert.Equal(expected, result);
        Assert.True(WindowsFileNameHelper.IsValid(result));
    }

    [Theory]
    [InlineData('ä')]
    [InlineData('a')]
    public void Sanitize_TruncatesToUtf8ByteLimit_KeepingExtension(char c)
    {
        var result = WindowsFileNameHelper.Sanitize(new string(c, 300) + ".txt");

        Assert.True(Encoding.UTF8.GetByteCount(result) <= WindowsFileNameHelper.MaxNameLength);
        Assert.EndsWith(".txt", result);
        Assert.True(WindowsFileNameHelper.IsValid(result));
    }

    [Fact]
    public void Sanitize_NeverSplitsSurrogatePairs()
    {
        var result = WindowsFileNameHelper.Sanitize(string.Concat(Enumerable.Repeat("😀", 100)));

        var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
        Assert.Equal(result, strict.GetString(strict.GetBytes(result)));
        Assert.True(Encoding.UTF8.GetByteCount(result) <= WindowsFileNameHelper.MaxNameLength);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("bad:dir/file?.txt", "bad_dir/file_.txt")]
    [InlineData("ok/CON/x.", "ok/CON_/x")]
    [InlineData("Photos/2024/a.jpg", "Photos/2024/a.jpg")]
    public void SanitizeRelativePath_SanitizesEachSegment(string path, string expected)
        => Assert.Equal(expected, WindowsFileNameHelper.SanitizeRelativePath(path));

    [Fact]
    public void NumberedVariants_FollowExplorerStyle()
        => Assert.Equal(
            ["a_b (2).txt", "a_b (3).txt"],
            WindowsFileNameHelper.NumberedVariants("a_b.txt").Take(2));

    // Existing entries may predate the rule; they keep their name, only new segments are renamed.
    [Theory]
    [InlineData("x:y/z", "", "x_y/z")]
    [InlineData("old:dir/new:file", "old:dir", "old:dir/new_file")]
    [InlineData("old:file", "old:file", "old:file")]
    [InlineData("ok/a?b", "", "ok/a_b")]
    public void SanitizeNewSegments_KeepsExistingEntries(string path, string existing, string expected)
        => Assert.Equal(expected, WindowsFileNameHelper.SanitizeNewSegments(path, p => p == existing));
}
