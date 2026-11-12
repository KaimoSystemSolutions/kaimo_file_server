using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Xunit;
using Kaimo_File_Server.Infrastructure.Services.Files;

namespace Kaimo_File_Server.Infrastructure.Tests.Services.Files;

/// <summary>Startup initialization: the home-folder share is created on the first pool without any admin action.</summary>
public sealed partial class HomeDirectoryServiceTests
{
    [Fact]
    public async Task InitializeAsync_NotConfigured_CreatesShareOnFirstPoolAndProvisionsHomes()
    {
        var alice = AddUser("alice");
        var pool1 = Path.Combine(_root, "pool01");
        var pool2 = Path.Combine(_root, "pool02");
        Directory.CreateDirectory(pool1);
        Directory.CreateDirectory(pool2);

        await _sut.InitializeAsync([pool2, pool1]);

        var share = await _sut.GetHomesShareAsync();
        Assert.NotNull(share);
        Assert.Equal(Path.Combine(pool1, HomeDirectoryService.ShareName), share.Path);
        Assert.True(share.IsEnabled);
        Assert.True(Directory.Exists(Path.Combine(share.Path, HomeDirectoryService.HomePathOf(alice.Id))));
    }

    [Fact]
    public async Task InitializeAsync_AlreadyConfigured_KeepsTheCurrentPool()
    {
        var alice = AddUser("alice");
        var poolA = Path.Combine(_root, "pool-a");
        var poolB = Path.Combine(_root, "pool-b");
        Directory.CreateDirectory(poolA);
        Directory.CreateDirectory(poolB);
        await _sut.ConfigureAsync(poolB, alice.Id);

        await _sut.InitializeAsync([poolA, poolB]);

        Assert.Single(_shares);
        Assert.Equal(Path.Combine(poolB, HomeDirectoryService.ShareName), _shares[0].Path);
        Assert.False(Directory.Exists(Path.Combine(poolA, HomeDirectoryService.ShareName)));
    }

    [Fact]
    public async Task InitializeAsync_AlreadyConfigured_ProvisionsHomesOfNewUsers()
    {
        var alice = AddUser("alice");
        await _sut.ConfigureAsync(_root, alice.Id);
        var bob = AddUser("bob");

        await _sut.InitializeAsync([_root]);

        var share = await _sut.GetHomesShareAsync();
        Assert.True(Directory.Exists(Path.Combine(share!.Path, HomeDirectoryService.HomePathOf(bob.Id))));
    }

    [Fact]
    public async Task InitializeAsync_NoExistingPool_CreatesNothing()
    {
        // A pool that is not mounted must never be recreated on the root file system.
        var missing = Path.Combine(_root, "not-mounted");

        await _sut.InitializeAsync([missing]);
        await _sut.InitializeAsync([]);

        Assert.Null(await _sut.GetHomesShareAsync());
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public async Task InitializeAsync_NameTakenByARegularShare_LeavesItAlone()
    {
        var pool = Path.Combine(_root, "pool01");
        Directory.CreateDirectory(pool);
        _shares.Add(new ShareDefinition(
            HomeDirectoryService.ShareName, Path.Combine(_root, "other"), WellKnownGUIDs.DEPARTMENT_GLOBAL,
            isEnabled: true, isShareHidden: false, isRecycleEnabled: false));

        await _sut.InitializeAsync([pool]);

        Assert.Null(await _sut.GetHomesShareAsync());
        Assert.Single(_shares);
    }
}
