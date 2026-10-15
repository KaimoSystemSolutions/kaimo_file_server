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
    [InlineData("a/b:c")]
    [InlineData("a/b*")]
    [InlineData("ends-with-dot.")]
    [InlineData("ends-with-space ")]
    public void TryNormalize_RejectsTraversalAndInvalidNames(string raw)
        => Assert.False(UploadFolderPath.TryNormalize(raw, out _));

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
