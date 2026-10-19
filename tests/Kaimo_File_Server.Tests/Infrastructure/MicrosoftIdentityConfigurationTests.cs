using System.Net;
using System.Text;
using Kaimo_File_Server.Infrastructure.Clouds;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class MicrosoftIdentityConfigurationTests
{
    [Fact]
    public async Task TenantOwnedPublicClient_IsUsedForTokenRefresh()
    {
        var identity = MicrosoftIdentityConfiguration.Create(
            "6d16b597-ef1c-4f57-b1f1-5dc0407e9f98", "contoso.onmicrosoft.com");
        string? requestBody = null;
        Uri? tokenEndpoint = null;
        var handler = new StubHttpMessageHandler(async request =>
        {
            if (request.RequestUri!.Host == "login.microsoftonline.com")
            {
                tokenEndpoint = request.RequestUri;
                requestBody = await request.Content!.ReadAsStringAsync();
                return Json(HttpStatusCode.OK, """{"access_token":"token","expires_in":3600}""");
            }

            return Json(HttpStatusCode.OK, """{"displayName":"Ada"}""");
        });
        using var http = new HttpClient(handler);
        await using var connection = new OneDriveConnection(
            new Dictionary<string, string> { ["refreshToken"] = "refresh" },
            http,
            identity: identity);

        await connection.GetAccountInfoAsync();

        Assert.Equal("/contoso.onmicrosoft.com/oauth2/v2.0/token", tokenEndpoint!.AbsolutePath);
        Assert.Contains("client_id=6d16b597-ef1c-4f57-b1f1-5dc0407e9f98", requestBody);
        Assert.DoesNotContain("client_secret", requestBody);
    }

    [Theory]
    [InlineData("not-a-guid", "common")]
    [InlineData("6d16b597-ef1c-4f57-b1f1-5dc0407e9f98", "https://untrusted.example/token")]
    [InlineData("6d16b597-ef1c-4f57-b1f1-5dc0407e9f98", "tenant/path")]
    public void InvalidPublicClientConfiguration_FailsClosed(string clientId, string authority)
        => Assert.Throws<InvalidOperationException>(() =>
            MicrosoftIdentityConfiguration.Create(clientId, authority));

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string body)
        => new(statusCode) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => send(request);
    }
}
