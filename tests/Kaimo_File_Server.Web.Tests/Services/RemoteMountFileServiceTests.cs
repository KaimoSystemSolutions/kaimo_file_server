using System.Text;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Storage;
using Kaimo_File_Server.Web.Services;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.Services;

public sealed class RemoteMountFileServiceTests : IDisposable
{
    private readonly MountTestHarness _h = new();

    public RemoteMountFileServiceTests()
    {
        _h.Remote.Directories.Add("root/docs");
        _h.Remote.Files["root/a.txt"] = "hello"u8.ToArray();
        _h.Remote.Files["root/docs/b.txt"] = "12345"u8.ToArray();
    }

    public void Dispose() => _h.Dispose();

    private static MemoryStream Body(string text) => new(Encoding.UTF8.GetBytes(text));

    private static async Task<string> ReadAsync(Stream stream)
    {
        await using (stream)
        using (var reader = new StreamReader(stream))
            return await reader.ReadToEndAsync();
    }

    // ── path mapping ─────────────────────────────────────────────────

    [Fact]
    public async Task List_ReturnsHostRelativePaths()
    {
        var items = await _h.NewAdapter().ListAsync("Team/Cloud", _h.User);
        Assert.Equal(["Team/Cloud/a.txt", "Team/Cloud/docs"], items.Select(i => i.Path).Order());
    }

    [Fact]
    public async Task List_AddressedInOtherCase_StillReportsCanonicalMountPath()
    {
        var items = await _h.NewAdapter().ListAsync("team/cloud/docs", _h.User);
        Assert.Equal("Team/Cloud/docs/b.txt", Assert.Single(items).Path);
    }

    [Theory]
    [InlineData("Team/Cloud/../secret")]
    [InlineData("Team/Other/a.txt")]
    [InlineData("Team/Cloud2/a.txt")]
    public async Task PathsOutsideTheMount_AreRefused(string path)
        => await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _h.NewAdapter().ListAsync(path, _h.User));

    // ── metadata ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetMetadata_MountRoot_IsDirectoryNamedAfterTheMount()
    {
        var meta = await _h.NewAdapter().GetMetadataAsync("Team/Cloud", _h.User);
        Assert.True(meta.IsDirectory);
        Assert.Equal("Cloud", meta.Name);
        Assert.Equal("Team/Cloud", meta.Path);
    }

    [Fact]
    public async Task GetMetadata_File_CarriesSizeAndHostPath()
    {
        var meta = await _h.NewAdapter().GetMetadataAsync("Team/Cloud/docs/b.txt", _h.User);
        Assert.Equal(5, meta.Size);
        Assert.Equal("Team/Cloud/docs/b.txt", meta.Path);
    }

    [Fact]
    public async Task GetMetadata_Missing_ThrowsFileNotFound()
        => await Assert.ThrowsAsync<FileNotFoundException>(
            () => _h.NewAdapter().GetMetadataAsync("Team/Cloud/nope.txt", _h.User));

    [Fact]
    public async Task FindEntry_BelowMissingFolder_IsNull_EvenThoughTheProviderOnlySaysIOException()
        => Assert.Null(await _h.NewAdapter().FindEntryAsync("Team/Cloud/missing/deep/x.txt", _h.User));

    [Fact]
    public async Task List_MissingFolder_ThrowsDirectoryNotFound()
        => await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => _h.NewAdapter().ListAsync("Team/Cloud/missing", _h.User));

    [Fact]
    public async Task List_ProviderOutage_IsNotReportedAsMissing()
    {
        _h.Remote.ListFailure = _ => new IOException("connection lost");
        var error = await Assert.ThrowsAsync<IOException>(() => _h.NewAdapter().ListAsync("Team/Cloud/docs", _h.User));
        Assert.IsNotType<DirectoryNotFoundException>(error);
        Assert.Equal("connection lost", error.Message);
    }

    [Fact]
    public async Task FindEntry_ProviderOutage_PropagatesInsteadOfSayingMissing()
        => await AssertOutageAsync(a => a.FindEntryAsync("Team/Cloud/a.txt", _h.User));

    private async Task AssertOutageAsync(Func<RemoteMountFileService, Task> action)
    {
        _h.Remote.ListFailure = _ => new IOException("connection lost");
        var error = await Assert.ThrowsAnyAsync<IOException>(() => action(_h.NewAdapter()));
        Assert.Equal("connection lost", error.Message);
    }

    // ── provider failures ────────────────────────────────────────────

    [Fact]
    public async Task RemoteAccessDenied_BecomesUnauthorized()
    {
        _h.Remote.ListFailure = _ => new RemoteStorageAccessDeniedException("denied");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _h.NewAdapter().ListAsync("Team/Cloud", _h.User));
    }

    [Fact]
    public async Task ForeignProviderException_BecomesRemoteShareUnavailable()
    {
        _h.Remote.ListFailure = _ => new HttpRequestException("boom");
        await Assert.ThrowsAsync<RemoteShareUnavailableException>(() => _h.NewAdapter().ListAsync("Team/Cloud", _h.User));
    }

    [Theory]
    [InlineData(StorageConnectionState.Disabled)]
    [InlineData(StorageConnectionState.NeedsReauthorization)]
    public async Task ConnectionNotReady_BecomesRemoteShareUnavailable(StorageConnectionState state)
    {
        _h.Connection.State = state;
        await Assert.ThrowsAsync<RemoteShareUnavailableException>(() => _h.NewAdapter().ListAsync("Team/Cloud", _h.User));
    }

    // ── read ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadFile_PassesTheProviderStreamThrough(bool seekable)
    {
        _h.Remote.SeekableReads = seekable;
        var stream = await _h.NewAdapter(CloudAccessPermission.Read).ReadFileAsync("Team/Cloud/a.txt", _h.User, preserveAccessTime: true);
        Assert.Equal(seekable, stream.CanSeek);
        Assert.Equal("hello", await ReadAsync(stream));
    }

    [Fact]
    public async Task DirectorySize_WalksTheTree()
        => Assert.Equal(10, await _h.NewAdapter().GetDirectorySizeAsync("Team/Cloud", _h.User));

    [Fact]
    public async Task Listing_IsServedFromTheSharedCache()
    {
        var adapter = _h.NewAdapter();
        await adapter.ListAsync("Team/Cloud", _h.User);
        await adapter.ListAsync("Team/Cloud", _h.User);
        Assert.Equal(1, _h.Remote.ListCalls["root"]);
    }

    [Fact]
    public async Task CacheHit_OpensNoSession()
    {
        await _h.NewAdapter().ListAsync("Team/Cloud", _h.User);
        var opened = _h.SessionsOpened;
        await _h.NewAdapter().ListAsync("Team/Cloud", _h.User);
        Assert.Equal(opened, _h.SessionsOpened);
    }

    [Fact]
    public async Task VersionsAndSnapshots_AreEmpty()
    {
        var adapter = _h.NewAdapter();
        Assert.Empty(await adapter.GetFileVersionsAsync("Team/Cloud/a.txt", _h.User));
        Assert.Empty(await adapter.GetSnapshotTimestampsAsync(_h.User));
        Assert.Empty(await adapter.GetFolderSnapshotTimestampsAsync("Team/Cloud", _h.User));
        Assert.Empty(await adapter.GetFolderSnapshotAsync("Team/Cloud", DateTime.UtcNow, _h.User));
    }

    [Fact]
    public async Task ReadVersion_IsNotFound_SoTheRestEndpointAnswers404()
        => await Assert.ThrowsAsync<FileNotFoundException>(
            () => _h.NewAdapter().ReadFileVersionAsync("Team/Cloud/a.txt", DateTime.UtcNow, _h.User));

    [Fact]
    public async Task FilterReadable_ReturnsAllPaths()
    {
        var readable = await _h.NewAdapter(CloudAccessPermission.Read).FilterReadablePathsAsync(
            [("Team/Cloud/a.txt", false), ("Team/Cloud/docs", true)], _h.User);
        Assert.Equal(2, readable.Count);
    }

    [Fact]
    public void ToAbsolutePath_IsNotSupported()
        => Assert.Throws<NotSupportedException>(() => _h.NewAdapter().ToAbsolutePath("Team/Cloud/a.txt"));

    // ── write ────────────────────────────────────────────────────────

    [Fact]
    public async Task Write_StoresBelowTheRemoteRoot_AndInvalidatesTheCache()
    {
        var adapter = _h.NewAdapter();
        await adapter.ListAsync("Team/Cloud", _h.User);

        await adapter.WriteFileAsync("Team/Cloud/new.txt", Body("x"), _h.User);

        Assert.True(_h.Remote.Files.ContainsKey("root/new.txt"));
        Assert.Contains(await adapter.ListAsync("Team/Cloud", _h.User), i => i.Name == "new.txt");
    }

    [Fact]
    public async Task Write_StampsTheSourceTimes_ThroughTheFileTimesOverload()
    {
        var modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var created = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        IFileService adapter = _h.NewAdapter();

        await adapter.WriteFileAsync("Team/Cloud/new.txt", Body("x"), _h.User, new FileTimes(created, modified, null));

        Assert.Equal(("root/new.txt", modified, (DateTime?)created, false), Assert.Single(_h.Remote.Stamped));
    }

    [Fact]
    public async Task Write_StampFailure_IsBestEffort()
    {
        _h.Remote.StampFailure = new IOException("no times here");
        await _h.NewAdapter().WriteFileAsync("Team/Cloud/new.txt", Body("x"), _h.User, DateTime.UtcNow, CancellationToken.None);
        Assert.True(_h.Remote.Files.ContainsKey("root/new.txt"));
    }

    [Fact]
    public async Task Write_OnReadOnlyMount_IsRefused()
        => await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _h.NewAdapter(CloudAccessPermission.Read).WriteFileAsync("Team/Cloud/new.txt", Body("x"), _h.User));

    [Fact]
    public async Task Write_OntoTheMountRoot_IsRefused()
        => await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _h.NewAdapter().WriteFileAsync("Team/Cloud", Body("x"), _h.User));

    [Fact]
    public async Task Write_ProviderFailure_StillInvalidatesTheCache()
    {
        var adapter = _h.NewAdapter();
        await adapter.ListAsync("Team/Cloud", _h.User);
        _h.Remote.WriteFailure = new IOException("disk full");

        await Assert.ThrowsAsync<IOException>(() => adapter.WriteFileAsync("Team/Cloud/x.txt", Body("x"), _h.User));
        await adapter.ListAsync("Team/Cloud", _h.User);

        Assert.Equal(2, _h.Remote.ListCalls["root"]);
    }

    [Fact]
    public async Task CreateDirectory_CreatesBelowTheRemoteRoot()
    {
        await _h.NewAdapter().CreateDirectoryAsync("Team/Cloud/docs/sub", _h.User);
        Assert.Contains("root/docs/sub", _h.Remote.Directories);
    }

    [Fact]
    public async Task Delete_IsPermanentAndRecursive_EvenWithRecycleBinRequested()
    {
        await _h.NewAdapter().DeleteFileAsync("Team/Cloud/docs", _h.User, isRecycleEnabled: true);
        Assert.False(_h.Remote.Files.ContainsKey("root/docs/b.txt"));
        Assert.DoesNotContain("root/docs", _h.Remote.Directories);
    }

    [Fact]
    public async Task Delete_MountRoot_IsRefused()
        => await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _h.NewAdapter().DeleteFileAsync("Team/Cloud", _h.User, false));

    [Fact]
    public async Task Rename_MovesWithinTheRemote()
    {
        await _h.NewAdapter().RenameAsync("Team/Cloud/a.txt", "Team/Cloud/docs/c.txt", _h.User);
        Assert.True(_h.Remote.Files.ContainsKey("root/docs/c.txt"));
        Assert.False(_h.Remote.Files.ContainsKey("root/a.txt"));
    }

    [Theory]
    [InlineData("Team/Cloud", "Team/Cloud2")]
    [InlineData("Team/Cloud", "Team/Cloud/x")]
    [InlineData("Team/Cloud/a.txt", "Team/Cloud")]
    public async Task Rename_InvolvingTheMountRootOrLeavingTheMount_IsRefused(string from, string to)
        => await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _h.NewAdapter().RenameAsync(from, to, _h.User));

    // ── capability clamp ─────────────────────────────────────────────

    [Fact]
    public async Task ProviderWithoutDelete_RefusesDelete()
    {
        _h.Capabilities = MountTestHarness.AllCapabilities & ~StorageProviderCapabilities.Delete;
        var adapter = _h.NewAdapter();
        Assert.False(await adapter.CanDeleteAsync("Team/Cloud/a.txt", _h.User));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => adapter.DeleteFileAsync("Team/Cloud/a.txt", _h.User, false));
        Assert.True(_h.Remote.Files.ContainsKey("root/a.txt"));
    }

    [Fact]
    public async Task ProviderWithoutRename_RefusesSameFolderRename_ButMoveCapabilityStillMoves()
    {
        _h.Capabilities = MountTestHarness.AllCapabilities & ~StorageProviderCapabilities.Rename;
        var adapter = _h.NewAdapter();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => adapter.RenameAsync("Team/Cloud/a.txt", "Team/Cloud/b.txt", _h.User));
        await adapter.RenameAsync("Team/Cloud/a.txt", "Team/Cloud/docs/a.txt", _h.User);
    }

    [Theory]
    [InlineData(CloudAccessPermission.Read, false)]
    [InlineData(CloudAccessPermission.Write, true)]
    public async Task CanWriteFamily_FollowsThePermission(CloudAccessPermission permission, bool expected)
    {
        var adapter = _h.NewAdapter(permission);
        Assert.True(await adapter.CanReadAsync("Team/Cloud/a.txt", _h.User));
        Assert.True(await adapter.CanListAsync("Team/Cloud", _h.User));
        Assert.Equal(expected, await adapter.CanWriteAsync("Team/Cloud/a.txt", _h.User));
        Assert.Equal(expected, await adapter.CanCreateAsync("Team/Cloud/n.txt", _h.User));
        Assert.Equal(expected, await adapter.CanDeleteAsync("Team/Cloud/a.txt", _h.User));
    }

    [Fact]
    public async Task CanDelete_MountRoot_IsFalse()
        => Assert.False(await _h.NewAdapter().CanDeleteAsync("Team/Cloud", _h.User));

    // ── times ────────────────────────────────────────────────────────

    [Fact]
    public async Task SetTimes_OnDirectory_UsesTheDirectoryVariant()
    {
        var modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        IFileService adapter = _h.NewAdapter();
        await adapter.SetTimesAsync("Team/Cloud/docs", _h.User, new FileTimes(null, modified, null));
        Assert.Equal(("root/docs", modified, (DateTime?)null, true), Assert.Single(_h.Remote.Stamped));
    }

    [Fact]
    public async Task SetTimes_WithoutModifiedTime_IsANoOp()
    {
        IFileService adapter = _h.NewAdapter();
        await adapter.SetTimesAsync("Team/Cloud/a.txt", _h.User, new FileTimes(DateTime.UtcNow, null, null));
        Assert.Empty(_h.Remote.Stamped);
    }

    [Fact]
    public async Task SetTimes_OnMountRoot_IsANoOp()
    {
        await _h.NewAdapter().SetModifiedAtAsync("Team/Cloud", _h.User, DateTime.UtcNow);
        Assert.Empty(_h.Remote.Stamped);
    }

    [Fact]
    public async Task SetTimes_OnReadOnlyMount_IsRefused()
        => await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _h.NewAdapter(CloudAccessPermission.Read).SetModifiedAtAsync("Team/Cloud/a.txt", _h.User, DateTime.UtcNow));

    // ── exists probe used by the write-target sanitizer ──────────────

    [Theory]
    [InlineData("Team", true)]
    [InlineData("Team/Cloud", true)]
    [InlineData("Team/Cloud/a.txt", true)]
    [InlineData("Team/Cloud/nope", false)]
    public async Task Exists_AnswersForMountAncestorsAndContent(string path, bool expected)
        => Assert.Equal(expected, await _h.NewAdapter().ExistsAsync(path, _h.User));

    [Fact]
    public async Task Exists_ProviderFailure_IsFalse()
    {
        _h.Remote.ListFailure = _ => new IOException("connection lost");
        Assert.False(await _h.NewAdapter().ExistsAsync("Team/Cloud/a.txt", _h.User));
    }

    // ── lifetime ─────────────────────────────────────────────────────

    [Fact]
    public async Task Session_IsOpenedOnceAndDisposedWithTheAdapter()
    {
        await using (var adapter = _h.NewAdapter())
        {
            await adapter.ListAsync("Team/Cloud", _h.User);
            await adapter.ListAsync("Team/Cloud/docs", _h.User);
            await adapter.WriteFileAsync("Team/Cloud/x.txt", Body("x"), _h.User);
        }
        Assert.Equal(1, _h.SessionsOpened);
        Assert.Equal(1, _h.SessionsDisposed);
    }
}
