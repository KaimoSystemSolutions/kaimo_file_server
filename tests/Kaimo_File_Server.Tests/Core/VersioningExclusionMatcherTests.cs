using Kaimo_File_Server.Core.Services.File;
using Xunit;

namespace Kaimo_File_Server.Tests;

public class VersioningExclusionMatcherTests
{
    private static readonly VersioningExclusionMatcher Defaults =
        new(VersioningPolicy.DefaultExcludePatterns);

    [Theory]
    [InlineData("~$report.docx")]
    [InlineData("projects/2026/~$plan.xlsx")]
    [InlineData("upload.TMP")]
    [InlineData("a/b/c.temp")]
    [InlineData("video.mp4.part")]
    [InlineData("setup.exe.crdownload")]
    [InlineData("docs/.~lock.letter.odt#")]
    [InlineData(".notes.txt.swp")]
    [InlineData("photos/Thumbs.db")]
    [InlineData(".DS_Store")]
    [InlineData("music/desktop.ini")]
    public void DefaultPatterns_ExcludeTemporaryAndSystemFiles(string path)
        => Assert.True(Defaults.IsExcluded(path));

    [Theory]
    [InlineData("tmpfile.txt")]
    [InlineData("report.docx")]
    [InlineData("temp/report.docx")]
    [InlineData("my~$file.docx")]
    [InlineData("archive.tmp.zip")]
    public void DefaultPatterns_DoNotExcludeRegularFiles(string path)
        => Assert.False(Defaults.IsExcluded(path));

    [Fact]
    public void PatternWithSlash_MatchesTheShareRelativePath()
    {
        var matcher = new VersioningExclusionMatcher(["build/*", "/cache/**"]);

        Assert.True(matcher.IsExcluded("build/output.dll"));
        Assert.False(matcher.IsExcluded("build/sub/output.dll"));
        Assert.False(matcher.IsExcluded("src/build/output.dll"));
        Assert.True(matcher.IsExcluded("cache/a/b/c.bin"));
    }

    [Fact]
    public void QuestionMark_MatchesExactlyOneCharacter()
    {
        var matcher = new VersioningExclusionMatcher(["file?.log"]);

        Assert.True(matcher.IsExcluded("file1.log"));
        Assert.False(matcher.IsExcluded("file10.log"));
    }

    [Fact]
    public void RegexMetacharacters_AreLiteral()
    {
        var matcher = new VersioningExclusionMatcher(["a+(b).txt"]);

        Assert.True(matcher.IsExcluded("a+(b).txt"));
        Assert.False(matcher.IsExcluded("aa(b).txt"));
    }

    [Fact]
    public void NoPatterns_ExcludesNothing()
        => Assert.False(new VersioningExclusionMatcher([]).IsExcluded("~$x.docx"));

    [Fact]
    public void BackslashSeparators_AreNormalized()
        => Assert.True(Defaults.IsExcluded("folder\\~$x.docx"));
}
