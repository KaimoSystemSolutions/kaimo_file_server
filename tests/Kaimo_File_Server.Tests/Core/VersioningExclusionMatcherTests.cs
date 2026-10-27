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
    [InlineData("docs/.~lock.letter.odt#")]
    [InlineData(".notes.txt.swp")]
    [InlineData("photos/Thumbs.db")]
    [InlineData(".DS_Store")]
    [InlineData("music/desktop.ini")]
    public void DefaultPatterns_ExcludeTemporaryAndSystemFiles(string path)
        => Assert.True(Defaults.IsExcluded(path));

    [Theory]
    [InlineData("videos/Holiday.MP4")]
    [InlineData("clips/talk.mkv")]
    [InlineData("vm/disk.vhdx")]
    [InlineData("install/ubuntu.iso")]
    [InlineData("downloads/archive.zip")]
    [InlineData("backup/logs.tar.gz")]
    [InlineData("tools/setup.exe")]
    [InlineData("tools/installer.msi")]
    [InlineData("photos/IMG_0001.jpg")]
    [InlineData("photos/IMG_0002.HEIC")]
    [InlineData("raw/DSC_0001.nef")]
    [InlineData("music/song.mp3")]
    [InlineData("music/track.flac")]
    [InlineData("docs/invoice.pdf")]
    public void DefaultPatterns_ExcludeBulkMediaArchivesAndExecutables(string path)
        => Assert.True(Defaults.IsExcluded(path));

    [Theory]
    [InlineData("tmpfile.txt")]
    [InlineData("report.docx")]
    [InlineData("temp/report.docx")]
    [InlineData("my~$file.docx")]
    [InlineData("report.tmp.docx")]
    [InlineData("design/poster.psd")]
    [InlineData("~WRD0000.tmp")]
    [InlineData("docs/lu12345abc.tmp")]
    [InlineData("video.mp4.part")]
    [InlineData("setup.exe.crdownload")]
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

    [Theory]
    [InlineData("node_modules/lib/index.js", true)]
    [InlineData("web/node_modules/lib/index.js", true)]
    [InlineData("my_node_modules/index.js", false)]
    [InlineData("web/my_node_modules/index.js", false)]
    public void LeadingDoubleStarSlash_AlsoMatchesAtTheShareRoot(string path, bool expected)
        => Assert.Equal(expected, new VersioningExclusionMatcher(["**/node_modules/**"]).IsExcluded(path));

    [Theory]
    [InlineData("docs/a.bak", true)]
    [InlineData("docs/x/y/a.bak", true)]
    [InlineData("docsa.bak", false)]
    public void InnerDoubleStarSlash_MatchesZeroOrMoreFolders(string path, bool expected)
        => Assert.Equal(expected, new VersioningExclusionMatcher(["docs/**/*.bak"]).IsExcluded(path));

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
