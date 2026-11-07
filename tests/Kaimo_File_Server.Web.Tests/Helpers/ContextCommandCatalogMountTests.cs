using Kaimo_File_Server.Web.DynamicHelpers;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.Helpers;

/// <summary>A mounted virtual-share entry offers only commands that never touch the host folder.</summary>
public sealed class ContextCommandCatalogMountTests
{
    [Theory]
    [InlineData("open")]
    [InlineData("permissions")]
    [InlineData("newfolder")]
    [InlineData("refresh")]
    public void SafeCommands_AreOfferedOnMountEntries(string id)
        => Assert.True(ContextCommandCatalog.AllowedOnMountEntry(id));

    [Theory]
    [InlineData("rename")]
    [InlineData("delete")]
    [InlineData("copy")]
    [InlineData("cut")]
    [InlineData("download")]
    [InlineData("compress.zip")]
    [InlineData("properties")]
    [InlineData("sharelink")]
    public void HostCommands_AreWithheldOnMountEntries(string id)
        => Assert.False(ContextCommandCatalog.AllowedOnMountEntry(id));
}
