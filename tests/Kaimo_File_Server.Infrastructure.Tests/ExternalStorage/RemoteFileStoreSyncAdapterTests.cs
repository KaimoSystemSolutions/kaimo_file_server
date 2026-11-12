using Kaimo_File_Server.Core.Services.CloudSync;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Infrastructure.ExternalStorage;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Infrastructure.Tests.ExternalStorage;

/// <summary>The protocol adapter forwards creation times in both sync directions.</summary>
public sealed class RemoteFileStoreSyncAdapterTests
{
    private static readonly DateTime Modified = new(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Created = new(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    [Fact]
    public async Task UploadAsync_WithCreationTime_StampsBothTimes()
    {
        var store = new Mock<IRemoteFileStore>();
        ICloudConnection adapter = new RemoteFileStoreSyncAdapter("smb", store.Object);

        await adapter.UploadAsync("/Team/a.txt", new MemoryStream([1]), Modified, Created);

        store.Verify(s => s.WriteAsync("/Team/a.txt", It.IsAny<Stream>(), true, It.IsAny<CancellationToken>()));
        store.Verify(s => s.TrySetTimesAsync("/Team/a.txt", Modified, Created, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task UploadAsync_WithoutCreationTime_StampsOnlyTheModifiedTime()
    {
        var store = new Mock<IRemoteFileStore>();
        ICloudConnection adapter = new RemoteFileStoreSyncAdapter("smb", store.Object);

        await adapter.UploadAsync("/Team/a.txt", new MemoryStream([1]), Modified);

        store.Verify(s => s.TrySetTimesAsync("/Team/a.txt", Modified, null, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task UploadAsync_FailedTimeStamp_DoesNotFailTheUpload()
    {
        var store = new Mock<IRemoteFileStore>();
        store.Setup(s => s.TrySetTimesAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("utimes not supported"));
        ICloudConnection adapter = new RemoteFileStoreSyncAdapter("smb", store.Object);

        await adapter.UploadAsync("/Team/a.txt", new MemoryStream([1]), Modified, Created);
    }

    [Fact]
    public async Task TrySetDirectoryTimesAsync_ForwardsTheCreationTime()
    {
        var store = new Mock<IRemoteFileStore>();
        ICloudConnection adapter = new RemoteFileStoreSyncAdapter("smb", store.Object);

        await adapter.TrySetDirectoryTimesAsync("/Team/dir", Modified, Created);

        store.Verify(s => s.TrySetDirectoryTimesAsync("/Team/dir", Modified, Created, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task ListAsync_MapsTheRemoteCreationTime()
    {
        var store = new Mock<IRemoteFileStore>();
        store.Setup(s => s.ListAsync("/Team", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new RemoteStorageItem("a.txt", "/Team/a.txt", false, 1, Modified, CreatedAtUtc: Created),
                new RemoteStorageItem("b.txt", "/Team/b.txt", false, 1, Modified)
            ]);
        ICloudConnection adapter = new RemoteFileStoreSyncAdapter("smb", store.Object);

        var items = await adapter.ListAsync("/Team");

        Assert.Equal(Created, items[0].CreatedAt);
        Assert.Null(items[1].CreatedAt);
    }
}
