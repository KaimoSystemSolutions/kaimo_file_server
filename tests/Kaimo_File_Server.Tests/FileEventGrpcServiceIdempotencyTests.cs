using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
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
    public async Task CloseUsesOpaqueCapture_AndCompletedRetryDoesNotReopenIt()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "kaimo-close-test-" + Guid.NewGuid().ToString("N"));
        var captureId = Guid.NewGuid().ToString("N");
        var capturePath = Path.Combine(
            root, ".kaimo-close-captures", captureId + ".cap");
        Directory.CreateDirectory(Path.GetDirectoryName(capturePath)!);
        await File.WriteAllBytesAsync(capturePath, [4, 3, 2, 1]);
        try
        {
            var share = new ShareDefinition("docs", root);
            var shares = new Mock<IShareRepository>();
            shares.Setup(x => x.GetByNameAsync("docs")).ReturnsAsync(share);
            var user = new User(
                Guid.NewGuid(), "Alice", "alice", "hash", "nt");
            var auth = new Mock<IAuthenticationLookup>();
            auth.Setup(x => x.ResolveUserContextAsync("alice"))
                .ReturnsAsync(new UserContext(user, [], [], []));
            byte[]? received = null;
            var fileService = new Mock<IFileService>();
            fileService.Setup(x => x.ToAbsolutePath(
                    $".kaimo-close-captures/{captureId}.cap"))
                .Returns(capturePath);
            fileService.Setup(x => x.NotifyExternalCloseAsync(
                    "file.txt", It.IsAny<UserContext>(),
                    It.IsAny<Func<Task<Stream>>>()))
                .Returns(async (
                    string _, UserContext _, Func<Task<Stream>> openCapture) =>
                {
                    await using var stream = await openCapture();
                    using var copy = new MemoryStream();
                    await stream.CopyToAsync(copy);
                    received = copy.ToArray();
                });
            var factory = new Mock<IFileServiceFactory>();
            factory.Setup(x => x.CreateForShare(share.Id, share.Path))
                .Returns(fileService.Object);
            var receipts = new Mock<ISambaLifecycleEventRepository>();
            receipts.SetupSequence(x => x.TryClaimAsync(
                    It.IsAny<Guid>(), "close", It.IsAny<TimeSpan>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(SambaEventClaimResult.Acquired)
                .ReturnsAsync(SambaEventClaimResult.AlreadyCompleted);
            receipts.Setup(x => x.CompleteAsync(
                    It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            var service = new FileEventGrpcService(
                factory.Object, shares.Object, auth.Object, receipts.Object,
                NullLogger<FileEventGrpcService>.Instance);
            var request = new NotifyCloseRequest
            {
                EventId = Guid.NewGuid().ToString("N"),
                Username = "alice",
                Share = "docs",
                Path = "file.txt",
                CaptureId = captureId
            };

            Assert.True((await service.NotifyClose(request, null!)).Ok);
            File.Delete(capturePath);
            Assert.True((await service.NotifyClose(request, null!)).Ok);

            Assert.Equal(new byte[] { 4, 3, 2, 1 }, received);
            fileService.Verify(x => x.NotifyExternalCloseAsync(
                "file.txt", It.IsAny<UserContext>(),
                It.IsAny<Func<Task<Stream>>>()), Times.Once);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

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
            Username = "alice",
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

    [Theory]
    [InlineData(false, "old.txt", "new.txt")]
    [InlineData(true, "old", "new")]
    public async Task RenameThreadsEventIdAndObjectTypeIntoLifecycleTransition(
        bool isDirectory,
        string oldPath,
        string newPath)
    {
        var share = new ShareDefinition("docs", Path.GetTempPath());
        var shares = new Mock<IShareRepository>();
        shares.Setup(x => x.GetByNameAsync("docs")).ReturnsAsync(share);
        var fileService = new Mock<IFileService>();
        var eventId = Guid.NewGuid();
        fileService.Setup(x => x.NotifyExternalRenameAsync(
                oldPath, newPath, isDirectory, eventId))
            .Returns(Task.CompletedTask);
        var factory = new Mock<IFileServiceFactory>();
        factory.Setup(x => x.CreateForShare(share.Id, share.Path))
            .Returns(fileService.Object);
        var receipts = new Mock<ISambaLifecycleEventRepository>();
        receipts.Setup(x => x.TryClaimAsync(
                eventId, "rename", It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SambaEventClaimResult.Acquired);
        receipts.Setup(x => x.CompleteAsync(
                eventId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var service = new FileEventGrpcService(
            factory.Object, shares.Object, Mock.Of<IAuthenticationLookup>(),
            receipts.Object, NullLogger<FileEventGrpcService>.Instance);

        var reply = await service.NotifyRename(
            new NotifyRenameRequest
            {
                EventId = eventId.ToString("N"),
                Username = "alice",
                Share = "docs",
                OldPath = oldPath,
                NewPath = newPath,
                IsDirectory = isDirectory
            },
            null!);

        Assert.True(reply.Ok);
        fileService.Verify(x => x.NotifyExternalRenameAsync(
            oldPath, newPath, isDirectory, eventId), Times.Once);
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

    [Fact]
    public async Task CloseWithoutValidCaptureId_IsRejectedBeforeSideEffects()
    {
        var factory = new Mock<IFileServiceFactory>();
        var shares = new Mock<IShareRepository>();
        var auth = new Mock<IAuthenticationLookup>();
        var receipts = new Mock<ISambaLifecycleEventRepository>();
        var service = new FileEventGrpcService(
            factory.Object, shares.Object, auth.Object, receipts.Object,
            NullLogger<FileEventGrpcService>.Instance);

        var reply = await service.NotifyClose(
            new NotifyCloseRequest
            {
                EventId = Guid.NewGuid().ToString("N"),
                Share = "docs",
                Path = "file.txt",
                CaptureId = "../live-file"
            },
            null!);

        Assert.False(reply.Ok);
        factory.VerifyNoOtherCalls();
        shares.VerifyNoOtherCalls();
        auth.VerifyNoOtherCalls();
        receipts.VerifyNoOtherCalls();
    }
}
