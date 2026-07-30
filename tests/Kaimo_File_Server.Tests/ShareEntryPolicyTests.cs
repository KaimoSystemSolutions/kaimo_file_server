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
}
