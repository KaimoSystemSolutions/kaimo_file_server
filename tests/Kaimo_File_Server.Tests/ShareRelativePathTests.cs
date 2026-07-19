using Kaimo_File_Server.Core.Helpers;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Guards the single source of truth for path normalization. The traversal
/// rules here are the first line of defense against escaping a share root,
/// so a regression that loosens them is a security regression.
/// </summary>
public class ShareRelativePathTests
{
    // ─────────────── Normalize ───────────────

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("/", "")]
    [InlineData(@"\docs\sub\file.txt", "docs/sub/file.txt")]
    [InlineData("docs/sub/", "docs/sub")]
    [InlineData("/leading", "leading")]
    [InlineData("trailing/", "trailing")]
    [InlineData("double//slash", "double/slash")]
    public void Normalize_ProducesCanonicalForm(string? input, string expected)
        => Assert.Equal(expected, ShareRelativePath.Normalize(input));

    // ─────────────── IsValid: legitimate paths ───────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("file.txt")]
    [InlineData("docs/sub/file.txt")]
    [InlineData("report..final.txt")]   // ".." inside a segment is fine
    [InlineData("..gitignore")]         // leading dots in a name are fine
    [InlineData("a..b/c..d")]
    [InlineData("weird. ..name")]
    public void IsValid_AllowsLegitimatePaths(string? path)
        => Assert.True(ShareRelativePath.IsValid(path));

    // ─────────────── IsValid: traversal / injection ───────────────

    [Theory]
    [InlineData("..")]
    [InlineData("../")]
    [InlineData("../secret")]
    [InlineData("sub/../../etc")]
    [InlineData("./../../etc/shadow")]
    [InlineData(@"..\..\windows")]      // backslashes normalize to '/'
    [InlineData("a/b/../../../c")]
    public void IsValid_RejectsTraversalSegments(string path)
        => Assert.False(ShareRelativePath.IsValid(path));

    [Fact]
    public void IsValid_RejectsNulByte()
    {
        Assert.False(ShareRelativePath.IsValid("file\0.txt"));
        Assert.False(ShareRelativePath.IsValid("dir/\0"));
    }

    [Fact]
    public void IsValid_DotSegmentAlone_IsAllowed()
    {
        // A bare "." is current-directory and collapses harmlessly; only ".."
        // escapes upward, so "." must NOT be treated as traversal.
        Assert.True(ShareRelativePath.IsValid("./file.txt"));
    }
}
