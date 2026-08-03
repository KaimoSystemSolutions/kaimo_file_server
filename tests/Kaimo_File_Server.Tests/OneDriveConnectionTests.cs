using System.Net;
using System.Text;
using Kaimo_File_Server.Infrastructure.Clouds;
using Microsoft.Extensions.Configuration;
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
        var connection = new OneDriveConnection(data, CreateConfiguration(), http);

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
        var connection = new OneDriveConnection(CreateData(), CreateConfiguration(), http);

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
        var connection = new OneDriveConnection(CreateData(), CreateConfiguration(), http);
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
        var connection = new OneDriveConnection(CreateData(), CreateConfiguration(), http);
        await using var data = new MemoryStream(new byte[10 * 1024 * 1024 + 1]);

        await connection.UploadAsync("large.bin", data, new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal(
            ["bytes 0-10485759/10485761", "bytes 10485760-10485760/10485761"],
            uploadRanges);
        await connection.Dispose();
    }

    [Theory]
    [InlineData("common")]
    [InlineData("organizations")]
    [InlineData("consumers")]
    [InlineData("d917bd9f-c0f1-4e26-a9d5-94aa5c43b218")]
    public void ValidateTenant_AcceptsSupportedMicrosoftAuthorities(string tenant)
        => Assert.Equal(tenant, OneDriveConnection.ValidateTenant(tenant));

    [Fact]
    public void ValidateTenant_RejectsArbitraryAuthorityPath()
        => Assert.Throws<InvalidOperationException>(() =>
            OneDriveConnection.ValidateTenant("common/oauth2/evil"));

    private static IConfiguration CreateConfiguration()
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OneDriveOAuth:ClientId"] = "client-id",
                ["OneDriveOAuth:Tenant"] = "common"
            })
            .Build();

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
}
