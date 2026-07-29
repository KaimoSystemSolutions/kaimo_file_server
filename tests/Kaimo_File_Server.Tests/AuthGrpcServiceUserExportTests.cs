using Grpc.Core;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.SmbBridge.Grpc;
using Kaimo_File_Server.SmbBridge.Security;
using Kaimo_File_Server.SmbBridge.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class AuthGrpcServiceUserExportTests
{
    [Fact]
    public async Task ListUsers_ExportsOneBoundedPageWithContinuationMetadata()
    {
        var auth = new Mock<IAuthenticationLookup>();
        auth.Setup(lookup => lookup.GetSambaCredentialBatchAsync(
                0,
                AuthGrpcService.MaximumPageSize,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SambaCredentialBatch(
                [
                    new SambaCredential(
                        "alice",
                        Convert.FromHexString(
                            "0123456789ABCDEF0123456789ABCDEF")),
                ],
                ["broken"],
                AuthGrpcService.MaximumPageSize,
                HasMore: true));
        var sut = BuildSut(auth);

        ListUsersReply reply = await sut.ListUsers(
            new ListUsersRequest
            {
                PageSize = AuthGrpcService.MaximumPageSize,
            },
            new UnitServerCallContext());

        Assert.Equal("alice", Assert.Single(reply.Users).Username);
        Assert.Equal((uint)1, reply.RejectedUsers);
        Assert.Equal(
            (uint)AuthGrpcService.MaximumPageSize,
            reply.NextOffset);
        Assert.True(reply.HasMore);
        Assert.NotEmpty(reply.ContinuationToken);
        auth.Verify(lookup => lookup.GetSambaCredentialBatchAsync(
            0,
            AuthGrpcService.MaximumPageSize,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ListUsers_RejectsOversizedPageBeforeAuthenticationLookup()
    {
        var auth = new Mock<IAuthenticationLookup>();
        var sut = BuildSut(auth);

        RpcException exception = await Assert.ThrowsAsync<RpcException>(
            () => sut.ListUsers(
                new ListUsersRequest
                {
                    PageSize = AuthGrpcService.MaximumPageSize + 1,
                },
                new UnitServerCallContext()));

        Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
        auth.Verify(
            lookup => lookup.GetSambaCredentialBatchAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ListUsers_RejectsMissingPageSizeForRollingUpgradeSafety()
    {
        var auth = new Mock<IAuthenticationLookup>();
        var sut = BuildSut(auth);

        RpcException exception = await Assert.ThrowsAsync<RpcException>(
            () => sut.ListUsers(
                new ListUsersRequest(),
                new UnitServerCallContext()));

        Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
    }

    [Fact]
    public async Task ListUsers_RejectsUInt32BoundsAsInvalidArguments()
    {
        var auth = new Mock<IAuthenticationLookup>();
        var sut = BuildSut(auth);

        RpcException pageException = await Assert.ThrowsAsync<RpcException>(
            () => sut.ListUsers(
                new ListUsersRequest { PageSize = uint.MaxValue },
                new UnitServerCallContext()));
        RpcException offsetException = await Assert.ThrowsAsync<RpcException>(
            () => sut.ListUsers(
                new ListUsersRequest { Offset = uint.MaxValue },
                new UnitServerCallContext()));

        Assert.Equal(StatusCode.InvalidArgument, pageException.StatusCode);
        Assert.Equal(StatusCode.InvalidArgument, offsetException.StatusCode);
    }

    [Fact]
    public async Task ListUsers_FailsClosedWhenAnotherPageWouldExceedTotalLimit()
    {
        var auth = new Mock<IAuthenticationLookup>();
        auth.Setup(lookup => lookup.GetSambaCredentialBatchAsync(
                AuthGrpcService.MaximumExportUsers - 1,
                1,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SambaCredentialBatch(
                [],
                [],
                SourceCount: 1,
                HasMore: true));
        var limiter = BuildRateLimiter();
        var sut = BuildSut(auth, limiter);
        string token = limiter.CreateContinuationToken(
            "unknown",
            AuthGrpcService.MaximumExportUsers - 1);

        RpcException exception = await Assert.ThrowsAsync<RpcException>(
            () => sut.ListUsers(
                new ListUsersRequest
                {
                    Offset = AuthGrpcService.MaximumExportUsers - 1,
                    PageSize = 1,
                    ContinuationToken = token,
                },
                new UnitServerCallContext()));

        Assert.Equal(StatusCode.ResourceExhausted, exception.StatusCode);
    }

    [Fact]
    public async Task ListUsers_FailsClosedWhenFinalPageCrossesTotalLimit()
    {
        var auth = new Mock<IAuthenticationLookup>();
        auth.Setup(lookup => lookup.GetSambaCredentialBatchAsync(
                AuthGrpcService.MaximumExportUsers - 1,
                2,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SambaCredentialBatch(
                [],
                [],
                SourceCount: 2,
                HasMore: false));
        var limiter = BuildRateLimiter();
        var sut = BuildSut(auth, limiter);

        RpcException exception = await Assert.ThrowsAsync<RpcException>(
            () => sut.ListUsers(
                new ListUsersRequest
                {
                    Offset = AuthGrpcService.MaximumExportUsers - 1,
                    PageSize = 2,
                    ContinuationToken = limiter.CreateContinuationToken(
                        "unknown",
                        AuthGrpcService.MaximumExportUsers - 1),
                },
                new UnitServerCallContext()));

        Assert.Equal(StatusCode.ResourceExhausted, exception.StatusCode);
    }

    private static AuthGrpcService BuildSut(
        Mock<IAuthenticationLookup> authenticationLookup) =>
        BuildSut(authenticationLookup, BuildRateLimiter());

    private static AuthGrpcService BuildSut(
        Mock<IAuthenticationLookup> authenticationLookup,
        HashExportRateLimiter rateLimiter) =>
        new(
            authenticationLookup.Object,
            rateLimiter,
            NullLogger<AuthGrpcService>.Instance);

    private static HashExportRateLimiter BuildRateLimiter() =>
        new(Options.Create(new HashExportRateLimitOptions
        {
            PermitLimit = 10,
            WindowSeconds = 60,
        }));

    private sealed class UnitServerCallContext : ServerCallContext
    {
        private readonly Metadata _requestHeaders = [];
        private readonly Metadata _responseTrailers = [];
        private Status _status;
        private WriteOptions? _writeOptions;

        protected override string MethodCore => "test";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "test";
        protected override DateTime DeadlineCore => DateTime.MaxValue;
        protected override Metadata RequestHeadersCore => _requestHeaders;
        protected override CancellationToken CancellationTokenCore =>
            CancellationToken.None;
        protected override Metadata ResponseTrailersCore => _responseTrailers;
        protected override Status StatusCore
        {
            get => _status;
            set => _status = value;
        }
        protected override WriteOptions? WriteOptionsCore
        {
            get => _writeOptions;
            set => _writeOptions = value;
        }
        protected override AuthContext AuthContextCore =>
            new("", new Dictionary<string, List<AuthProperty>>());

        protected override ContextPropagationToken CreatePropagationTokenCore(
            ContextPropagationOptions? options) =>
            throw new NotSupportedException();

        protected override Task WriteResponseHeadersAsyncCore(
            Metadata responseHeaders) => Task.CompletedTask;
    }
}
