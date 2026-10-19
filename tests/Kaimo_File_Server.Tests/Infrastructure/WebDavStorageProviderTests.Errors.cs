using System.Net;
using System.Text;
using System.Text.Json;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Infrastructure.ExternalStorage;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed partial class WebDavStorageProviderTests
{
    [Fact]
    public async Task EnsureSuccessOrThrow_AcceptsSuccessAndMultiStatus()
    {
        var ok = new HttpResponseMessage(HttpStatusCode.OK);
        var created = new HttpResponseMessage(HttpStatusCode.Created);
        var multiStatus = new HttpResponseMessage(HttpStatusCode.MultiStatus);

        await WebDavRemoteFileStore.EnsureSuccessOrThrowAsync(ok, CancellationToken.None);
        await WebDavRemoteFileStore.EnsureSuccessOrThrowAsync(created, CancellationToken.None);
        await WebDavRemoteFileStore.EnsureSuccessOrThrowAsync(multiStatus, CancellationToken.None);
    }

    [Fact]
    public async Task EnsureSuccessOrThrow_ThrowsAccessDeniedFor401And403()
    {
        var unauthorized = new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("access denied")
        };
        var forbidden = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("forbidden")
        };

        await Assert.ThrowsAsync<RemoteStorageAccessDeniedException>(
            () => WebDavRemoteFileStore.EnsureSuccessOrThrowAsync(unauthorized, CancellationToken.None));
        await Assert.ThrowsAsync<RemoteStorageAccessDeniedException>(
            () => WebDavRemoteFileStore.EnsureSuccessOrThrowAsync(forbidden, CancellationToken.None));
    }

    [Fact]
    public async Task EnsureSuccessOrThrow_ThrowsIOExceptionForNotFound()
    {
        var notFound = new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("not found"),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://example.com/missing.txt")
        };

        var exception = await Assert.ThrowsAsync<IOException>(
            () => WebDavRemoteFileStore.EnsureSuccessOrThrowAsync(notFound, CancellationToken.None));

        Assert.Contains("not found", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnsureSuccessOrThrow_ThrowsIOExceptionForConflict()
    {
        var conflict = new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent("conflict")
        };

        var exception = await Assert.ThrowsAsync<IOException>(
            () => WebDavRemoteFileStore.EnsureSuccessOrThrowAsync(conflict, CancellationToken.None));

        Assert.Contains("409", exception.Message);
    }

    [Fact]
    public async Task EnsureSuccessOrThrow_ThrowsIOExceptionForPreconditionFailed()
    {
        var preconditionFailed = new HttpResponseMessage(HttpStatusCode.PreconditionFailed)
        {
            Content = new StringContent("precondition failed")
        };

        var exception = await Assert.ThrowsAsync<IOException>(
            () => WebDavRemoteFileStore.EnsureSuccessOrThrowAsync(preconditionFailed, CancellationToken.None));

        Assert.Contains("already exists", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnsureSuccessOrThrow_ThrowsIOExceptionForInsufficientStorage()
    {
        var insufficient = new HttpResponseMessage(HttpStatusCode.InsufficientStorage)
        {
            Content = new StringContent("no space")
        };

        var exception = await Assert.ThrowsAsync<IOException>(
            () => WebDavRemoteFileStore.EnsureSuccessOrThrowAsync(insufficient, CancellationToken.None));

        Assert.Contains("507", exception.Message);
    }

    [Fact]
    public async Task EnsureSuccessOrThrow_TruncatesLongErrorBodies()
    {
        var error = new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent(new string('x', 2000))
        };

        var exception = await Assert.ThrowsAsync<IOException>(
            () => WebDavRemoteFileStore.EnsureSuccessOrThrowAsync(error, CancellationToken.None));

        Assert.True(exception.Message.Length < 600);
    }
}
