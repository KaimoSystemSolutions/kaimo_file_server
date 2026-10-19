using System.Collections.Generic;
using System.IO;
using Kaimo_File_Server.Core.Storage;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Custom pool-name resolution: a friendly name overrides the path-derived name,
/// while blank/missing entries fall back to the path's final component.
/// </summary>
public class StoragePoolNamingTests
{
    private static string Pool(string leaf)
        => Path.Combine(Path.GetTempPath(), "kaimo-pools", leaf);

    [Fact]
    public void Resolve_NoCustomName_UsesPathDerivedName()
    {
        var path = Pool("pool01");
        Assert.Equal("pool01", StoragePoolNaming.Resolve(null, path));
        Assert.Equal("pool01",
            StoragePoolNaming.Resolve(new Dictionary<string, string>(), path));
    }

    [Fact]
    public void Resolve_CustomName_OverridesAndTrims()
    {
        var path = Pool("pool01");
        var map = new Dictionary<string, string>
        {
            [StoragePoolNaming.NormalizeKey(path)] = "  SSD_Pool01  ",
        };
        Assert.Equal("SSD_Pool01", StoragePoolNaming.Resolve(map, path));
    }

    [Fact]
    public void Resolve_BlankCustomName_FallsBackToDerived()
    {
        var path = Pool("pool01");
        var map = new Dictionary<string, string>
        {
            [StoragePoolNaming.NormalizeKey(path)] = "   ",
        };
        Assert.Equal("pool01", StoragePoolNaming.Resolve(map, path));
    }
}
