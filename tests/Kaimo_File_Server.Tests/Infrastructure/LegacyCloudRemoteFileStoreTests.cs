using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Infrastructure.ExternalStorage;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Cloud Access downloads are spooled to disk instead of memory, and a rotated
/// token is written back without resurrecting a connection that was disabled or
/// re-authorized in the meantime.
/// </summary>
public sealed class LegacyCloudRemoteFileStoreTests
{
    private readonly StorageConnection _connection = new()
    {
        Id = Guid.NewGuid(), ProviderId = "onedrive", Name = "OneDrive",
        State = StorageConnectionState.Ready, ConcurrencyVersion = 3
    };
    private readonly Mock<ICloudConnection> _cloud = new();
    private readonly Mock<IStorageConnectionRepository> _repository = new();

    [Fact]
    public async Task OpenRead_SpoolsToSelfDeletingTempFile()
    {
        byte[] payload = [1, 2, 3, 4, 5];
        _cloud.Setup(c => c.DownloadAsync("/a.bin", It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Returns<string, Stream, CancellationToken>((_, target, ct) => target.WriteAsync(payload, ct).AsTask());

        string path;
        await using (var stream = await Store().OpenReadAsync("/a.bin"))
        {
            var file = Assert.IsType<FileStream>(stream);
            path = file.Name;
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            Assert.Equal(payload, copy.ToArray());
        }

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task OpenRead_FailedDownload_LeavesNoTempFile()
    {
        string? path = null;
        _cloud.Setup(c => c.DownloadAsync("/a.bin", It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Returns<string, Stream, CancellationToken>((_, target, _) =>
            {
                path = ((FileStream)target).Name;
                throw new IOException("network");
            });

        await Assert.ThrowsAsync<IOException>(() => Store().OpenReadAsync("/a.bin"));

        Assert.NotNull(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Rotation_IsWrittenConditionally_AndNeverTouchesState()
    {
        ArrangeRotation();
        _repository.Setup(r => r.TryUpdateCredentialAsync(
                _connection.Id, It.IsAny<long>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var store = Store();

        await store.CreateDirectoryAsync("/one");
        await store.CreateDirectoryAsync("/two");

        // Each successful write advances the expected version for the next one.
        _repository.Verify(r => r.TryUpdateCredentialAsync(
            _connection.Id, 3, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(r => r.TryUpdateCredentialAsync(
            _connection.Id, 4, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(r => r.UpdateRuntimeAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<StorageConnectionState>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Rotation_OnConcurrentlyChangedConnection_StaysPending()
    {
        ArrangeRotation();
        _repository.Setup(r => r.TryUpdateCredentialAsync(
                It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await Store().CreateDirectoryAsync("/one");

        _cloud.Verify(c => c.AcknowledgeCredentialChanges(), Times.Never);
    }

    private void ArrangeRotation()
    {
        _cloud.SetupGet(c => c.HasPendingCredentialChanges).Returns(true);
        _cloud.Setup(c => c.GetPendingCredentialChanges())
            .Returns(new Dictionary<string, string> { ["refreshToken"] = "rotated" });
    }

    private LegacyCloudRemoteFileStore Store() => new(
        _connection,
        new Dictionary<string, string> { ["refreshToken"] = "t1" },
        _cloud.Object,
        new DataProtectionCredentialVault(new EphemeralDataProtectionProvider()),
        _repository.Object);
}
