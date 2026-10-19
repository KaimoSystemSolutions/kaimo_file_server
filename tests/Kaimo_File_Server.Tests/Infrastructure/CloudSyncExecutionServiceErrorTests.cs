using System.Net;
using System.Net.Sockets;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Infrastructure.Clouds;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class CloudSyncExecutionServiceErrorTests
{
    [Fact]
    public void LocalDirectoryMissing_MapsToLocalPathMissing()
    {
        Assert.Equal("local_path_missing",
            CloudSyncExecutionService.ClassifyError(
                new SyncDirectoryMissingException(SyncEndpoint.Local)));
        // A bare DirectoryNotFoundException (the reported crash) is also local, and
        // must win over its IOException base which maps to connection_failed.
        Assert.Equal("local_path_missing",
            CloudSyncExecutionService.ClassifyError(new DirectoryNotFoundException("2")));
    }

    [Fact]
    public void RemoteDirectoryMissing_MapsToRemotePathMissing()
    {
        Assert.Equal("remote_path_missing",
            CloudSyncExecutionService.ClassifyError(
                new SyncDirectoryMissingException(SyncEndpoint.Remote)));
        Assert.Equal("remote_path_missing",
            CloudSyncExecutionService.ClassifyError(Provider(HttpStatusCode.NotFound, "http_404")));
    }

    [Fact]
    public void ConnectionFailures_MapToConnectionFailed()
    {
        Assert.Equal("connection_failed",
            CloudSyncExecutionService.ClassifyError(new HttpRequestException("boom")));
        Assert.Equal("connection_failed",
            CloudSyncExecutionService.ClassifyError(new SocketException()));
        Assert.Equal("connection_failed",
            CloudSyncExecutionService.ClassifyError(new TimeoutException()));
        Assert.Equal("connection_failed",
            CloudSyncExecutionService.ClassifyError(new IOException("reset")));
    }

    [Fact]
    public void AccessDenied_MapsToRemoteAccessDenied()
        => Assert.Equal("remote_access_denied",
            CloudSyncExecutionService.ClassifyError(
                new RemoteStorageAccessDeniedException("denied")));

    [Fact]
    public void ProviderError_KeepsItsSanitizedCode_ExceptNotFound()
    {
        // A non-404 provider error surfaces its own code unchanged...
        Assert.Equal("invalid_grant",
            CloudSyncExecutionService.ClassifyError(
                Provider(HttpStatusCode.Unauthorized, "invalid_grant")));
        // ...while a 500 stays a provider code rather than collapsing to connection_failed.
        Assert.Equal("http_500",
            CloudSyncExecutionService.ClassifyError(
                Provider(HttpStatusCode.InternalServerError, "http_500")));
    }

    [Fact]
    public void UnknownException_FallsBackToSyncFailed()
        => Assert.Equal("sync_failed",
            CloudSyncExecutionService.ClassifyError(new Exception("something else")));

    private static ProviderRequestException Provider(HttpStatusCode status, string code)
        => new("google", new SanitizedProviderError(code, ProviderErrorCategory.Unknown, status));
}
