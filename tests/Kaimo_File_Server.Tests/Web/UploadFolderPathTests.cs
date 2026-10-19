using Kaimo_File_Server.Web.Helpers;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// The folder part of a browser folder upload comes from the client; it must normalize
/// cleanly and never allow leaving the upload target.
/// </summary>
public class UploadFolderPathTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("Photos", "Photos")]
    [InlineData("/Photos/2024/", "Photos/2024")]
    [InlineData(@"Photos\2024", "Photos/2024")]
    [InlineData("Photos//2024", "Photos/2024")]
    [InlineData("report..final", "report..final")]
    public void TryNormalize_AcceptsValidFolders(string? raw, string expected)
    {
        Assert.True(UploadFolderPath.TryNormalize(raw, out var dir));
        Assert.Equal(expected, dir);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../x")]
    [InlineData("a/../../b")]
    [InlineData("a/./b")]
    public void TryNormalize_RejectsTraversal(string raw)
        => Assert.False(UploadFolderPath.TryNormalize(raw, out _));

    // Names from Linux/macOS clients are renamed to Windows-valid ones instead of failing.
    [Theory]
    [InlineData("a/b:c", "a/b_c")]
    [InlineData("a/b*", "a/b_")]
    [InlineData("ends-with-dot.", "ends-with-dot")]
    [InlineData("ends-with-space ", "ends-with-space")]
    [InlineData("CON/x", "CON_/x")]
    public void TryNormalize_SanitizesWindowsInvalidNames(string raw, string expected)
    {
        Assert.True(UploadFolderPath.TryNormalize(raw, out var dir, out var renamed));
        Assert.Equal(expected, dir);
        Assert.True(renamed);
    }

    [Fact]
    public void TryNormalize_ValidFolder_IsNotReportedAsRenamed()
    {
        Assert.True(UploadFolderPath.TryNormalize("Photos/2024", out _, out var renamed));
        Assert.False(renamed);
    }

    [Fact]
    public void TryNormalize_RejectsTooDeepPaths()
    {
        Assert.True(UploadFolderPath.TryNormalize("a/b/c", out _, maxDepth: 3));
        Assert.False(UploadFolderPath.TryNormalize("a/b/c/d", out _, maxDepth: 3));
    }

    [Fact]
    public void Prefixes_ListsParentsFirst()
    {
        Assert.Equal(["a", "a/b", "a/b/c"], UploadFolderPath.Prefixes("a/b/c"));
        Assert.Empty(UploadFolderPath.Prefixes(""));
    }

    [Fact]
    public void Unpack_ExpandsOneFolderPerFile_AndRejectsBadIndices()
        => Assert.Equal(
            ["a", "", "a", null, null],
            UploadFolderPath.Unpack(["a", ""], [0, 1, 0, 2, -1]));
}
