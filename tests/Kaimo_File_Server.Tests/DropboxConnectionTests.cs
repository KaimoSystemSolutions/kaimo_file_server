using System.Net;
using System.Text;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Clouds;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class DropboxConnectionTests
{
    private static readonly DropboxIdentityConfiguration Identity =
        DropboxIdentityConfiguration.Create("appkey123");

    [Fact]
    public async Task ListAsync_RefreshesTokenMapsEntriesAndSkipsDeleted()
    {
        var tokenRequestBody = string.Empty;
        var listPath = string.Empty;
        var handler = new StubHandler(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/oauth2/token")
            {
                tokenRequestBody = await request.Content!.ReadAsStringAsync();
                return Json(HttpStatusCode.OK, """{"access_token":"access-token","expires_in":14400}""");
            }

            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("access-token", request.Headers.Authorization?.Parameter);
            listPath = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK,
                """
                {
                  "entries": [
                    {".tag":"folder","name":"Reports","path_display":"/Team/Reports"},
                    {".tag":"file","name":"plan.txt","path_display":"/Team/plan.txt","size":42,"server_modified":"2026-08-02T12:00:00Z"},
                    {".tag":"deleted","name":"old.txt","path_display":"/Team/old.txt"}
                  ],
                  "has_more": false
                }
                """);
        });
        using var http = new HttpClient(handler);
        var connection = new DropboxConnection(CreateData(), http, Identity);

        var items = await connection.ListAsync("/Team");

        Assert.Contains("grant_type=refresh_token", tokenRequestBody);
        Assert.DoesNotContain("client_secret", tokenRequestBody);
        Assert.Contains("\"path\":\"/Team\"", listPath);
        Assert.Collection(
            items,
            directory =>
            {
                Assert.True(directory.IsDirectory);
                Assert.Equal("/Team/Reports", directory.Path);
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
    public async Task ListAsync_RootUsesEmptyDropboxPath()
    {
        var listPath = string.Empty;
        var handler = new StubHandler(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/oauth2/token")
                return Json(HttpStatusCode.OK, """{"access_token":"t","expires_in":14400}""");
            listPath = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, """{"entries":[],"has_more":false}""");
        });
        using var http = new HttpClient(handler);
        var connection = new DropboxConnection(CreateData(), http, Identity);

        await connection.ListAsync("/");

        Assert.Contains("\"path\":\"\"", listPath);
        await connection.Dispose();
    }

    [Fact]
    public async Task ProviderFailure_DoesNotExposeRawResponseBody()
    {
        const string secret = "provider-body-secret";
        var errorBody = "{\"error_summary\":\"path/not_found/\",\"error\":{\"" + secret + "\":true}}";
        var handler = new StubHandler(request => Task.FromResult(
            request.RequestUri!.AbsolutePath == "/oauth2/token"
                ? Json(HttpStatusCode.OK, """{"access_token":"t","expires_in":14400}""")
                : Json(HttpStatusCode.BadRequest, errorBody)));
        using var http = new HttpClient(handler);
        await using var connection = new DropboxConnection(CreateData(), http, Identity);

        var exception = await Assert.ThrowsAsync<ProviderRequestException>(
            () => connection.DownloadAsync("/missing.txt", new MemoryStream()));

        Assert.Equal("dropbox", exception.ProviderId);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
    }

    private static Dictionary<string, string> CreateData()
        => new()
        {
            ["refreshToken"] = "refresh-token",
            ["scope"] = DropboxOAuthDefaults.Scope
        };

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string json)
        => new(statusCode) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request);
    }
}
