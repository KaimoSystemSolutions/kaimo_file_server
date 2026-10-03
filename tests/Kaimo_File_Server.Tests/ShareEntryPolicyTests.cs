using Kaimo_File_Server.Core.Helpers;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class ShareEntryPolicyTests
{
    [Theory]
    [InlineData(".kaimo-close-captures")]
    [InlineData(".KAIMO-SNAPSHOTS/file.cap")]
    public void Classify_TopLevelInternalNamespace_IsHidden(string path)
    {
        var result = ShareEntryPolicy.Classify(path);

        Assert.Equal(ShareEntryKind.Internal, result.Kind);
        Assert.False(result.IsVisibleInFileBrowser);
    }

    [Fact]
    public void Classify_NestedKaimoName_RemainsRegularUserContent()
    {
        var result = ShareEntryPolicy.Classify(
            "documents/.kaimo-notes");

        Assert.Equal(ShareEntryKind.Regular, result.Kind);
        Assert.True(result.IsVisibleInFileBrowser);
    }

    [Theory]
    [InlineData(".RECYCLE_BIN")]
    [InlineData(".recycle_bin/folder/file.txt")]
    public void Classify_RecycleBin_IsVisibleSpecialEntry(string path)
    {
        var result = ShareEntryPolicy.Classify(path);

        Assert.Equal(ShareEntryKind.RecycleBin, result.Kind);
        Assert.True(result.IsVisibleInFileBrowser);
    }

    [Fact]
    public void Classify_SimilarRecyclePrefix_RemainsRegular()
    {
        var result = ShareEntryPolicy.Classify(
            ".RECYCLE_BIN_backup/file.txt");

        Assert.Equal(ShareEntryKind.Regular, result.Kind);
        Assert.True(result.IsVisibleInFileBrowser);
    }

    [Theory]
    [InlineData(".RECYCLE_BIN")]
    [InlineData(".RECYCLE_BIN/file.txt")]
    [InlineData(".kaimo-close-captures")]
    [InlineData(".kaimo-anything/file.txt")]
    public void IsReservedForUserWrites_SystemNamespace_ReturnsTrue(string path)
    {
        Assert.True(ShareEntryPolicy.IsReservedForUserWrites(path));
    }

    [Theory]
    [InlineData(".RECYCLE_BIN_backup")]
    [InlineData("documents/.RECYCLE_BIN")]
    [InlineData("documents/.kaimo-notes")]
    public void IsReservedForUserWrites_RegularNamespace_ReturnsFalse(string path)
    {
        Assert.False(ShareEntryPolicy.IsReservedForUserWrites(path));
    }

    // rootDepth = 1: the home-folder share, one recycle bin per <userId>.

    [Theory]
    [InlineData("uid/.RECYCLE_BIN", ShareEntryKind.RecycleBin)]
    [InlineData("uid/.recycle_bin/a/b.txt", ShareEntryKind.RecycleBin)]
    [InlineData("uid/.kaimo-x", ShareEntryKind.Internal)]
    [InlineData(".kaimo-x", ShareEntryKind.Internal)]         // share-root rules still hold
    [InlineData("uid", ShareEntryKind.Regular)]
    [InlineData("uid/docs/.RECYCLE_BIN", ShareEntryKind.Regular)]
    public void Classify_WithRootDepth_AppliesRulesBelowTheRoot(string path, ShareEntryKind expected)
    {
        Assert.Equal(expected, ShareEntryPolicy.Classify(path, rootDepth: 1).Kind);
    }

    [Theory]
    [InlineData("a/b.txt", 0, ".RECYCLE_BIN/a/b.txt")]
    [InlineData("uid/a/b.txt", 1, "uid/.RECYCLE_BIN/a/b.txt")]
    [InlineData("uid/b.txt", 1, "uid/.RECYCLE_BIN/b.txt")]
    public void GetRecyclePath_PlacesTheItemBelowItsRecycleRoot(string path, int depth, string expected)
    {
        Assert.Equal(expected, ShareEntryPolicy.GetRecyclePath(path, depth));
    }

    [Fact]
    public void TryNormalizeStrict_WithRootDepth_RejectsInternalNamespaceBelowTheRoot()
    {
        Assert.True(ShareRelativePath.TryNormalizeStrict("uid/.kaimo-x", out _));
        Assert.False(ShareRelativePath.TryNormalizeStrict("uid/.kaimo-x", out _, rootDepth: 1));
        Assert.True(ShareRelativePath.TryNormalizeStrict("uid/docs/.kaimo-x", out _, rootDepth: 1));
    }

    [Fact]
    public void GetRecyclePath_ForTheRecycleRootItself_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => ShareEntryPolicy.GetRecyclePath("uid", 1));
    }
}
