using System.Net;
using System.Text;
using Kaimo_File_Server.Infrastructure.Clouds;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class OneDriveConnectionTests
{
    [Fact]
    public async Task GetAccountInfoAsync_RefreshesTokenAndExposesRotationForPersistence()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(async request =>
        {
            requests.Add(await CloneAsync(request));
            if (request.RequestUri!.Host == "login.microsoftonline.com")
            {
                return Json(HttpStatusCode.OK,
                    """{"access_token":"access-token","expires_in":3600,"refresh_token":"new-refresh"}""");
            }

            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("access-token", request.Headers.Authorization?.Parameter);
            return Json(HttpStatusCode.OK,
                """{"displayName":"Ada Lovelace","mail":null,"userPrincipalName":"ada@example.test"}""");
        });
        var data = CreateData();
        using var http = new HttpClient(handler);
        var connection = new OneDriveConnection(data, http);

        var account = await connection.GetAccountInfoAsync();

        Assert.NotNull(account);
        Assert.Equal("Ada Lovelace", account.DisplayName);
        Assert.Equal("ada@example.test", account.Email);
        Assert.True(connection.HasPendingCredentialChanges);
        Assert.Equal("new-refresh", connection.GetPendingCredentialChanges()["refreshToken"]);
        var tokenRequestBody = await requests[0].Content!.ReadAsStringAsync();
        Assert.Contains("grant_type=refresh_token", tokenRequestBody);
        Assert.DoesNotContain("client_secret", tokenRequestBody);

        connection.AcknowledgeCredentialChanges();
        Assert.False(connection.HasPendingCredentialChanges);
        await connection.Dispose();
    }

    [Fact]
    public async Task Refresh_ReloadsLatestGrantAndPersistsRotationBeforeReleasingDistributedLease()
    {
        var tokenRequestBody = string.Empty;
        var lease = new TrackingLease();
        var persistedWhileOwned = false;
        var handler = new StubHttpMessageHandler(async request =>
        {
            if (request.RequestUri!.Host == "login.microsoftonline.com")
            {
                tokenRequestBody = await request.Content!.ReadAsStringAsync();
                return Json(HttpStatusCode.OK,
                    """{"access_token":"access","expires_in":3600,"refresh_token":"rotated-refresh"}""");
            }
            return Json(HttpStatusCode.OK, """{"displayName":"Ada"}""");
        });
        using var http = new HttpClient(handler);
        await using var connection = new OneDriveConnection(
            CreateData(),
            http,
            _ => Task.FromResult<IAsyncDisposable?>(lease),
            _ => Task.FromResult(new Dictionary<string, string>
            {
                ["refreshToken"] = "latest-refresh",
                ["scope"] = OneDriveOAuthDefaults.Scope
            }),
            (credentials, _) =>
            {
                persistedWhileOwned = !lease.IsDisposed
                                      && credentials["refreshToken"] == "rotated-refresh";
                return Task.CompletedTask;
            });

        await connection.GetAccountInfoAsync();

        Assert.Contains("refresh_token=latest-refresh", tokenRequestBody);
        Assert.True(persistedWhileOwned);
        Assert.True(lease.IsDisposed);
        Assert.False(connection.HasPendingCredentialChanges);
    }

    [Fact]
    public async Task ProviderFailure_DoesNotExposeRawResponseBodyOrToken()
    {
        const string secret = "provider-body-secret";
        var handler = new StubHttpMessageHandler(_ => Task.FromResult(Json(
            HttpStatusCode.BadRequest,
            $$"""{"error":"invalid_grant","error_description":"{{secret}}"}""")));
        using var http = new HttpClient(handler);
        await using var connection = new OneDriveConnection(CreateData(), http);

        var exception = await Assert.ThrowsAsync<Kaimo_File_Server.Core.Services.ProviderRequestException>(
            () => connection.GetAccountInfoAsync());

        Assert.Equal("invalid_grant", exception.ErrorCode);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedRotationWrite_IsRetriedBeforeAnotherProviderExchange()
    {
        var tokenRequests = 0;
        var persistenceAttempts = 0;
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host != "login.microsoftonline.com")
                return Task.FromResult(Json(HttpStatusCode.OK, """{"displayName":"Recovered"}"""));
            tokenRequests++;
            return Task.FromResult(Json(HttpStatusCode.OK,
                tokenRequests == 1
                    ? """{"access_token":"first","expires_in":3600,"refresh_token":"rotated"}"""
                    : """{"access_token":"second","expires_in":3600}"""));
        });
        using var http = new HttpClient(handler);
        await using var connection = new OneDriveConnection(
            CreateData(),
            http,
            _ => Task.FromResult<IAsyncDisposable?>(new TrackingLease()),
            _ => Task.FromResult(new Dictionary<string, string>
            {
                ["refreshToken"] = persistenceAttempts == 0 ? "old-refresh" : "rotated",
                ["scope"] = OneDriveOAuthDefaults.Scope
            }),
            (credentials, _) =>
            {
                persistenceAttempts++;
                Assert.Equal("rotated", credentials["refreshToken"]);
                if (persistenceAttempts == 1)
                    throw new IOException("Simulated database outage.");
                return Task.CompletedTask;
            });

        await Assert.ThrowsAsync<IOException>(() => connection.GetAccountInfoAsync());
        var account = await connection.GetAccountInfoAsync();

        Assert.Equal("Recovered", account!.DisplayName);
        Assert.Equal(2, persistenceAttempts);
        Assert.Equal(2, tokenRequests);
    }

    [Fact]
    public async Task ListAsync_EncodesPathAndMapsFilesAndFolders()
    {
        var graphUris = new List<Uri>();
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "login.microsoftonline.com")
            {
                return Task.FromResult(Json(HttpStatusCode.OK,
                    """{"access_token":"access-token","expires_in":3600}"""));
            }

            graphUris.Add(request.RequestUri);
            return Task.FromResult(Json(HttpStatusCode.OK,
                """
                {
                  "value": [
                    {"id":"1","name":"Reports","size":0,"lastModifiedDateTime":"2026-08-01T12:00:00Z","folder":{}},
                    {"id":"2","name":"plan.txt","size":42,"lastModifiedDateTime":"2026-08-02T12:00:00Z","file":{}},
                    {"id":"3","name":"Persönlicher Tresor","size":0,"lastModifiedDateTime":"2026-08-03T12:00:00Z","specialFolder":{"name":"vault"}}
                  ]
                }
                """));
        });
        using var http = new HttpClient(handler);
        var connection = new OneDriveConnection(CreateData(), http);

        var items = await connection.ListAsync("/Team Files/#Current");

        Assert.Contains("/root:/Team%20Files/%23Current:/children", graphUris.Single().AbsoluteUri);
        Assert.Contains("specialFolder", graphUris.Single().Query);
        Assert.Collection(
            items,
            directory =>
            {
                Assert.True(directory.IsDirectory);
                Assert.Equal("/Team Files/#Current/Reports", directory.Path);
            },
            file =>
            {
                Assert.False(file.IsDirectory);
                Assert.Equal(42, file.Size);
                Assert.Equal(DateTimeKind.Utc, file.ModifiedAt.Kind);
            });
        await connection.Dispose();
    }

    [Fact]
    public async Task ListAsync_PreCancelledRequestDoesNotContactMicrosoft()
    {
        var handler = new StubHttpMessageHandler(_ =>
            throw new InvalidOperationException("A cancelled request must not reach HTTP."));
        using var http = new HttpClient(handler);
        var connection = new OneDriveConnection(CreateData(), http);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            connection.ListAsync("/", cancellation.Token));

        await connection.Dispose();
    }

    [Fact]
    public async Task UploadAsync_UsesPreauthenticatedChunkedSessionForLargeFiles()
    {
        var uploadRanges = new List<string>();
        var handler = new StubHttpMessageHandler(async request =>
        {
            if (request.RequestUri!.Host == "login.microsoftonline.com")
            {
                return Json(HttpStatusCode.OK,
                    """{"access_token":"access-token","expires_in":3600}""");
            }

            if (request.RequestUri.Host == "upload.example.test")
            {
                Assert.Null(request.Headers.Authorization);
                uploadRanges.Add(request.Content!.Headers.ContentRange!.ToString());
                await request.Content.ReadAsByteArrayAsync();
                return uploadRanges.Count == 1
                    ? Json(HttpStatusCode.Accepted, """{"nextExpectedRanges":["10485760-"]}""")
                    : Json(HttpStatusCode.Created, """{"id":"uploaded-item"}""");
            }

            Assert.Equal("access-token", request.Headers.Authorization?.Parameter);
            if (request.Method == HttpMethod.Post)
            {
                Assert.EndsWith(":/createUploadSession", request.RequestUri.AbsolutePath);
                return Json(HttpStatusCode.OK,
                    """{"uploadUrl":"https://upload.example.test/session"}""");
            }

            Assert.Equal(HttpMethod.Patch, request.Method);
            Assert.EndsWith("/items/uploaded-item", request.RequestUri.AbsolutePath);
            return Json(HttpStatusCode.OK, "{}");
        });
        using var http = new HttpClient(handler);
        var connection = new OneDriveConnection(CreateData(), http);
        await using var data = new MemoryStream(new byte[10 * 1024 * 1024 + 1]);

        await connection.UploadAsync("large.bin", data, new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal(
            ["bytes 0-10485759/10485761", "bytes 10485760-10485760/10485761"],
            uploadRanges);
        await connection.Dispose();
    }

    [Fact]
    public async Task ResolveFolderByIdAsync_TracksFolderAfterExternalRename()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "login.microsoftonline.com")
                return Task.FromResult(Json(HttpStatusCode.OK, """{"access_token":"token","expires_in":3600}"""));
            Assert.EndsWith("/items/folder-id", request.RequestUri.AbsolutePath);
            return Task.FromResult(Json(HttpStatusCode.OK,
                """{"id":"folder-id","name":"Renamed","folder":{},"parentReference":{"path":"/drive/root:/Teams/Alpha"}}"""));
        });
        using var http = new HttpClient(handler);
        await using var connection = new OneDriveConnection(CreateData(), http);

        var folder = await connection.ResolveFolderByIdAsync("folder-id");

        Assert.Equal("folder-id", folder.ProviderId);
        Assert.Equal("Teams/Alpha/Renamed", folder.Path);
    }

    [Fact]
    public async Task MoveItemAsync_UsesProviderSidePatch()
    {
        var graphRequests = new List<(HttpMethod Method, string Path, string? Body)>();
        var handler = new StubHttpMessageHandler(async request =>
        {
            if (request.RequestUri!.Host == "login.microsoftonline.com")
                return Json(HttpStatusCode.OK, """{"access_token":"token","expires_in":3600}""");
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            graphRequests.Add((request.Method, request.RequestUri.AbsolutePath, body));
            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath.Contains("root:/docs/a.txt"))
                return Json(HttpStatusCode.OK, """{"id":"source-id"}""");
            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath.EndsWith("/drive/root"))
                return Json(HttpStatusCode.OK, """{"id":"root-id"}""");
            return Json(HttpStatusCode.OK, "{}");
        });
        using var http = new HttpClient(handler);
        await using var connection = new OneDriveConnection(CreateData(), http);

        await connection.MoveItemAsync("docs/a.txt", "a-renamed.txt");

        var patchRequest = Assert.Single(graphRequests, x => x.Method == HttpMethod.Patch);
        Assert.EndsWith("/items/source-id", patchRequest.Path);
        Assert.Contains("a-renamed.txt", patchRequest.Body);
        Assert.Contains("root-id", patchRequest.Body);
    }

    [Fact]
    public async Task CopyItemAsync_WaitsForProviderSideMonitorCompletion()
    {
        var monitorCalls = 0;
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "login.microsoftonline.com")
                return Task.FromResult(Json(HttpStatusCode.OK, """{"access_token":"token","expires_in":3600}"""));
            if (request.RequestUri.Host == "tenant.sharepoint.com")
            {
                monitorCalls++;
                return Task.FromResult(Json(HttpStatusCode.OK, """{"percentageComplete":100}"""));
            }
            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath.Contains("root:/docs/a.txt"))
                return Task.FromResult(Json(HttpStatusCode.OK, """{"id":"source-id"}"""));
            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath.EndsWith("/drive/root"))
                return Task.FromResult(Json(HttpStatusCode.OK, """{"id":"root-id"}"""));
            if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath.EndsWith("/copy"))
            {
                var accepted = Json(HttpStatusCode.Accepted, "{}");
                accepted.Headers.Location = new Uri("https://tenant.sharepoint.com/_api/v2.0/monitor/id");
                return Task.FromResult(accepted);
            }
            throw new InvalidOperationException($"Unexpected request {request.Method} {request.RequestUri}");
        });
        using var http = new HttpClient(handler);
        await using var connection = new OneDriveConnection(CreateData(), http);

        await connection.CopyItemAsync("docs/a.txt", "a-copy.txt");

        Assert.Equal(1, monitorCalls);
    }

    private static Dictionary<string, string> CreateData()
        => new()
        {
            ["connectionId"] = "connection-id",
            ["refreshToken"] = "old-refresh",
            ["scope"] = OneDriveOAuthDefaults.Scope
        };

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string json)
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        if (request.Content is not null)
            clone.Content = new StringContent(await request.Content.ReadAsStringAsync());
        return clone;
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => send(request);
    }

    private sealed class TrackingLease : IAsyncDisposable
    {
        public bool IsDisposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
