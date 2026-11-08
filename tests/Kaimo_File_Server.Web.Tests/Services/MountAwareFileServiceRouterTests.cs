using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Web.Services;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.Services;

public sealed class MountAwareFileServiceRouterTests : IDisposable
{
    private readonly MountTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Theory]
    [InlineData("Team/Cloud")]
    [InlineData("Team/Cloud/a/b.txt")]
    [InlineData("team/CLOUD/a")]
    public async Task PathInAVisibleMount_RoutesToTheAdapter(string path)
        => Assert.IsType<RemoteMountFileService>(await _h.NewRouter().ForPathAsync(_h.Host, path, _h.User));

    [Theory]
    [InlineData("")]
    [InlineData("Team")]
    [InlineData("Team/Cloud2")]
    public async Task PathOutsideMounts_RoutesToTheLocalService(string path)
        => Assert.IsNotType<RemoteMountFileService>(await _h.NewRouter().ForPathAsync(_h.Host, path, _h.User));

    [Fact]
    public async Task MountWithoutGrant_StaysLocal_AndHasNoEntry()
    {
        _h.Granted = null;
        var router = _h.NewRouter();
        Assert.IsNotType<RemoteMountFileService>(await router.ForPathAsync(_h.Host, "Team/Cloud", _h.User));
        Assert.Empty(await router.MountEntriesInAsync(_h.Host, "Team", _h.User));
    }

    [Fact]
    public async Task MountEntries_AreSyntheticFoldersOfTheParentOnly()
    {
        var router = _h.NewRouter();
        var entry = Assert.Single(await router.MountEntriesInAsync(_h.Host, "Team", _h.User));
        Assert.True(entry.IsDirectory);
        Assert.Equal("Cloud", entry.Name);
        Assert.Equal("Team/Cloud", entry.Path);
        Assert.Equal(DateTime.UnixEpoch, entry.ModifiedAt);
        Assert.Empty(await router.MountEntriesInAsync(_h.Host, "", _h.User));
    }

    [Fact]
    public async Task Mounts_AreLoadedOncePerRequestAndHost()
    {
        var router = _h.NewRouter();
        await router.ForPathAsync(_h.Host, "Team", _h.User);
        await router.ForPathAsync(_h.Host, "Team/Cloud", _h.User);
        await router.MountEntriesInAsync(_h.Host, "Team", _h.User);
        _h.Repository.Verify(r => r.GetMountedSharesAsync(_h.Host.Id, It.IsAny<CancellationToken>()), Times.Once);
        _h.Repository.Verify(r => r.GetSharesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FreshListings_ReachTheAdapter()
    {
        await _h.NewRouter().ForPathAsync(_h.Host, "Team/Cloud", _h.User);
        var router = _h.NewRouter();
        var cached = await router.ForPathAsync(_h.Host, "Team/Cloud", _h.User);
        await cached.ListAsync("Team/Cloud", _h.User);
        _h.Remote.Files["root/external.txt"] = [];

        var fresh = await _h.NewRouter().ForPathAsync(_h.Host, "Team/Cloud", _h.User, freshListings: true);

        Assert.NotNull(await fresh.FindEntryAsync("Team/Cloud/external.txt", _h.User));
    }

    [Fact]
    public async Task Router_DoesNotEvaluateTheManagementRight()
    {
        var mounts = await _h.NewResolver().GetVisibleAsync(_h.Host, _h.User, includeCanManage: false);
        Assert.False(Assert.Single(mounts).CanManage);
    }

    [Fact]
    public async Task SameBackend_YieldsTheSameInstance_SoCallersCanCompareBackends()
    {
        var router = _h.NewRouter();
        Assert.Same(await router.ForPathAsync(_h.Host, "Team/Cloud/a", _h.User),
            await router.ForPathAsync(_h.Host, "Team/Cloud/b", _h.User));
        Assert.Same(await router.ForPathAsync(_h.Host, "Team/a", _h.User),
            await router.ForPathAsync(_h.Host, "b", _h.User));
        Assert.NotSame(await router.ForPathAsync(_h.Host, "Team/Cloud/a", _h.User),
            await router.ForPathAsync(_h.Host, "Team/a", _h.User));
    }

    [Fact]
    public async Task AdapterCarriesTheEffectivePermission()
    {
        _h.Granted = CloudAccessPermission.Read;
        var adapter = await _h.NewRouter().ForPathAsync(_h.Host, "Team/Cloud", _h.User);
        Assert.False(await adapter.CanWriteAsync("Team/Cloud/a", _h.User));
    }

    [Fact]
    public async Task Dispose_ClosesTheOpenedSessions()
    {
        await using (var router = _h.NewRouter())
        {
            var adapter = await router.ForPathAsync(_h.Host, "Team/Cloud", _h.User);
            await adapter.ListAsync("Team/Cloud", _h.User);
        }
        Assert.Equal(1, _h.SessionsDisposed);
    }

    [Fact]
    public async Task HomesHost_HasNoMounts()
    {
        _h.Host.IsUserHomes = true;
        Assert.IsNotType<RemoteMountFileService>(await _h.NewRouter().ForPathAsync(_h.Host, "Team/Cloud", _h.User));
    }
}
