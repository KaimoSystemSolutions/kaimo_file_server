using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// <see cref="SharePoolMoveService"/>: the share move extracted from share management, shared
/// with the home-folder pool move. The share is disabled for the duration of the move so no
/// transport writes into a source that is about to disappear.
/// </summary>
public sealed class SharePoolMoveServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kaimo-move-" + Guid.NewGuid().ToString("N"));
    private readonly string _poolA;
    private readonly string _poolB;
    private readonly Mock<IShareRepository> _shares = new();
    private readonly List<bool> _enabledStates = [];
    private readonly List<string> _persistedPaths = [];
    private readonly SharePoolMoveService _sut;

    public SharePoolMoveServiceTests()
    {
        _poolA = Path.Combine(_root, "pool-a");
        _poolB = Path.Combine(_root, "pool-b");
        Directory.CreateDirectory(_poolA);
        Directory.CreateDirectory(_poolB);

        _shares.Setup(r => r.UpdateAsync(It.IsAny<ShareDefinition>()))
            .Callback<ShareDefinition>(s => _enabledStates.Add(s.IsEnabled))
            .Returns(Task.CompletedTask);
        _shares.Setup(r => r.UpdateLocationAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>()))
            .Callback<Guid, string, string>((_, _, path) => _persistedPaths.Add(path))
            .Returns(Task.CompletedTask);

        _sut = new SharePoolMoveService(_shares.Object, new ShareLockManager(), NullLogger<SharePoolMoveService>.Instance)
        {
            DisableGracePeriod = TimeSpan.Zero
        };
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private ShareDefinition Share(bool enabled = true)
    {
        var path = Path.Combine(_poolA, "docs");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "a.txt"), "content");
        return new ShareDefinition("docs", path, isEnabled: enabled);
    }

    [Fact]
    public async Task Move_RenamesPersistsAndDisablesDuringMove()
    {
        var share = Share();

        var outcome = await _sut.MoveAsync(share, _poolB);

        var destination = Path.Combine(_poolB, "docs");
        Assert.Equal(SharePoolMoveResult.Moved, outcome.Result);
        Assert.Equal("content", File.ReadAllText(Path.Combine(destination, "a.txt")));
        Assert.False(Directory.Exists(Path.Combine(_poolA, "docs")));
        Assert.Equal([destination], _persistedPaths);
        Assert.Equal(destination, share.Path);
        Assert.Equal([false, true], _enabledStates);
        Assert.True(share.IsEnabled);
    }

    [Fact]
    public async Task Move_DisabledShare_StaysDisabled()
    {
        var share = Share(enabled: false);

        var outcome = await _sut.MoveAsync(share, _poolB);

        Assert.Equal(SharePoolMoveResult.Moved, outcome.Result);
        Assert.Empty(_enabledStates);
        Assert.False(share.IsEnabled);
    }

    [Fact]
    public async Task Move_SamePool_IsUnchanged()
    {
        var share = Share();

        var outcome = await _sut.MoveAsync(share, _poolA);

        Assert.Equal(SharePoolMoveResult.Unchanged, outcome.Result);
        Assert.True(File.Exists(Path.Combine(_poolA, "docs", "a.txt")));
        Assert.Empty(_enabledStates);
    }

    [Fact]
    public async Task Move_DestinationExists_IsRefused()
    {
        var share = Share();
        Directory.CreateDirectory(Path.Combine(_poolB, "docs"));

        var outcome = await _sut.MoveAsync(share, _poolB);

        Assert.Equal(SharePoolMoveResult.DestinationExists, outcome.Result);
        Assert.True(File.Exists(Path.Combine(_poolA, "docs", "a.txt")));
        Assert.Empty(_persistedPaths);
    }

    [Fact]
    public async Task Move_SourceMissing_IsRefused()
    {
        var share = new ShareDefinition("ghost", Path.Combine(_poolA, "ghost"));

        var outcome = await _sut.MoveAsync(share, _poolB);

        Assert.Equal(SharePoolMoveResult.SourceMissing, outcome.Result);
    }

    [Fact]
    public async Task Move_DatabaseFailure_RollsBackRename()
    {
        var share = Share();
        var source = share.Path;
        _shares.Setup(r => r.UpdateLocationAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var outcome = await _sut.MoveAsync(share, _poolB);

        Assert.Equal(SharePoolMoveResult.Failed, outcome.Result);
        Assert.True(File.Exists(Path.Combine(source, "a.txt")));
        Assert.False(Directory.Exists(Path.Combine(_poolB, "docs")));
        Assert.Equal(source, share.Path);
        Assert.Equal([false, true], _enabledStates);
    }

    [Fact]
    public async Task Move_AcrossFileSystems_CopiesAndRemovesSource()
    {
        var share = Share();
        var source = share.Path;
        var sut = CrossFileSystemSut(available: long.MaxValue / 4);

        var outcome = await sut.MoveAsync(share, _poolB);

        Assert.Equal(SharePoolMoveResult.Moved, outcome.Result);
        Assert.Equal("content", File.ReadAllText(Path.Combine(_poolB, "docs", "a.txt")));
        Assert.False(Directory.Exists(source));
    }

    [Fact]
    public async Task Move_AcrossFileSystems_DatabaseFailure_DeletesCopyKeepsSource()
    {
        var share = Share();
        var source = share.Path;
        _shares.Setup(r => r.UpdateLocationAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("db down"));
        var sut = CrossFileSystemSut(available: long.MaxValue / 4);

        var outcome = await sut.MoveAsync(share, _poolB);

        Assert.Equal(SharePoolMoveResult.Failed, outcome.Result);
        Assert.True(File.Exists(Path.Combine(source, "a.txt")));
        Assert.False(Directory.Exists(Path.Combine(_poolB, "docs")));
        Assert.Equal(source, share.Path);
    }

    [Fact]
    public async Task Move_AcrossFileSystems_InsufficientSpace_IsRefused()
    {
        var share = Share();
        var sut = CrossFileSystemSut(available: 10);

        var outcome = await sut.MoveAsync(share, _poolB);

        Assert.Equal(SharePoolMoveResult.InsufficientSpace, outcome.Result);
        Assert.Equal(10, outcome.Available);
        Assert.True(outcome.Required > 10);
        Assert.True(File.Exists(Path.Combine(_poolA, "docs", "a.txt")));
        Assert.Equal([false, true], _enabledStates);
    }

    private SharePoolMoveService CrossFileSystemSut(long available)
        => new(_shares.Object, new ShareLockManager(), NullLogger<SharePoolMoveService>.Instance)
        {
            DisableGracePeriod = TimeSpan.Zero,
            RenameDirectory = (_, _) => throw new IOException("EXDEV"),
            AvailableFreeSpace = _ => available
        };
}
