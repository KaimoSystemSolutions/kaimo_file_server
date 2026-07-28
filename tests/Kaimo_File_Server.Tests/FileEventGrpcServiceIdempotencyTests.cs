using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.SmbBridge.Grpc;
using Kaimo_File_Server.SmbBridge.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class FileEventGrpcServiceIdempotencyTests
{
    [Fact]
    public async Task CompletedDuplicate_IsAcknowledgedWithoutRepeatingEffects()
    {
        var share = new ShareDefinition("docs", Path.GetTempPath());
        var shares = new Mock<IShareRepository>();
        shares.Setup(x => x.GetByNameAsync("docs")).ReturnsAsync(share);
        var auth = new Mock<IAuthenticationLookup>();
        var fileService = new Mock<IFileService>();
        fileService.Setup(x => x.NotifyExternalDeleteAsync("old.txt", false))
            .Returns(Task.CompletedTask);
        var factory = new Mock<IFileServiceFactory>();
        factory.Setup(x => x.CreateForShare(share.Id, share.Path))
            .Returns(fileService.Object);
        var receipts = new Mock<ISambaLifecycleEventRepository>();
        receipts.SetupSequence(x => x.TryClaimAsync(
                It.IsAny<Guid>(), "delete", It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SambaEventClaimResult.Acquired)
            .ReturnsAsync(SambaEventClaimResult.AlreadyCompleted);
        receipts.Setup(x => x.CompleteAsync(
                It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = new FileEventGrpcService(
            factory.Object, shares.Object, auth.Object, receipts.Object,
            NullLogger<FileEventGrpcService>.Instance);
        var request = new NotifyPathRequest
        {
            EventId = Guid.NewGuid().ToString("N"),
            Share = "docs",
            Path = "old.txt",
            IsDirectory = false
        };

        Assert.True((await service.NotifyDelete(request, null!)).Ok);
        Assert.True((await service.NotifyDelete(request, null!)).Ok);

        fileService.Verify(
            x => x.NotifyExternalDeleteAsync("old.txt", false), Times.Once);
        receipts.Verify(
            x => x.CompleteAsync(
                It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task MissingStableEventId_IsRejectedBeforeSideEffects()
    {
        var factory = new Mock<IFileServiceFactory>();
        var shares = new Mock<IShareRepository>();
        var auth = new Mock<IAuthenticationLookup>();
        var receipts = new Mock<ISambaLifecycleEventRepository>();
        var service = new FileEventGrpcService(
            factory.Object, shares.Object, auth.Object, receipts.Object,
            NullLogger<FileEventGrpcService>.Instance);

        var reply = await service.NotifyDelete(
            new NotifyPathRequest { Share = "docs", Path = "old.txt" },
            null!);

        Assert.False(reply.Ok);
        factory.VerifyNoOtherCalls();
        shares.VerifyNoOtherCalls();
        receipts.VerifyNoOtherCalls();
    }
}
