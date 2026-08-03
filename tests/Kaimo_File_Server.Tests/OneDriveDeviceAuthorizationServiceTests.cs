using System.Net;
using System.Text;
using Kaimo_File_Server.Web.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class OneDriveDeviceAuthorizationServiceTests
{
    [Fact]
    public async Task DeviceFlow_UsesPublicClientWithoutSecretOrRedirectUri()
    {
        var requestBodies = new List<string>();
        var handler = new StubHttpMessageHandler(async request =>
        {
            requestBodies.Add(await request.Content!.ReadAsStringAsync());
            if (request.RequestUri!.AbsolutePath.EndsWith("/devicecode", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK,
                    """
                    {
                      "device_code":"private-device-code",
                      "user_code":"ABCD-EFGH",
                      "verification_uri":"https://microsoft.com/devicelogin",
                      "expires_in":900,
                      "interval":5
                    }
                    """);
            }

            Assert.EndsWith("/token", request.RequestUri.AbsolutePath);
            return Json(HttpStatusCode.OK,
                """
                {
                  "access_token":"access-token",
                  "refresh_token":"refresh-token",
                  "scope":"Files.ReadWrite User.Read",
                  "expires_in":3600
                }
                """);
        });
        using var http = new HttpClient(handler);
        var service = new OneDriveDeviceAuthorizationService(
            CreateConfiguration("public-client-id"),
            new StubHttpClientFactory(http));
        var shareId = Guid.NewGuid();

        var authorization = await service.StartAsync(shareId, "documents", "ticket");
        var result = await service.PollAsync(authorization.SessionId);

        Assert.Equal("ABCD-EFGH", authorization.UserCode);
        Assert.Equal("https://microsoft.com/devicelogin", authorization.VerificationUri);
        Assert.Equal(OneDriveDevicePollState.Complete, result.State);
        Assert.Equal(shareId, result.ShareId);
        Assert.Equal("refresh-token", result.RefreshToken);
        Assert.Contains("client_id=public-client-id", requestBodies[0]);
        Assert.Contains("scope=offline_access+Files.ReadWrite+User.Read", requestBodies[0]);
        Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code", requestBodies[1]);
        Assert.All(requestBodies, body =>
        {
            Assert.DoesNotContain("client_secret", body);
            Assert.DoesNotContain("redirect_uri", body);
        });
    }

    [Fact]
    public async Task PollAsync_ReturnsPendingForMicrosoftAuthorizationPendingResponse()
    {
        var requestNumber = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            requestNumber++;
            return Task.FromResult(requestNumber == 1
                ? Json(HttpStatusCode.OK,
                    """{"device_code":"device","user_code":"CODE","verification_uri":"https://microsoft.com/devicelogin","expires_in":900,"interval":5}""")
                : Json(HttpStatusCode.BadRequest,
                    """{"error":"authorization_pending","error_description":"Waiting for the user."}"""));
        });
        using var http = new HttpClient(handler);
        var service = new OneDriveDeviceAuthorizationService(
            CreateConfiguration("public-client-id"),
            new StubHttpClientFactory(http));
        var authorization = await service.StartAsync(Guid.NewGuid(), "", "ticket");

        var result = await service.PollAsync(authorization.SessionId);

        Assert.Equal(OneDriveDevicePollState.Pending, result.State);
        Assert.Equal(5, result.RetryAfterSeconds);
    }

    [Fact]
    public async Task StartAsync_RejectsUnchangedClientIdPlaceholder()
    {
        using var http = new HttpClient(new StubHttpMessageHandler(_ =>
            throw new InvalidOperationException("No HTTP request was expected.")));
        var service = new OneDriveDeviceAuthorizationService(
            CreateConfiguration("PASTE_APPLICATION_CLIENT_ID_HERE"),
            new StubHttpClientFactory(http));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StartAsync(Guid.NewGuid(), "", "ticket"));
    }

    private static IConfiguration CreateConfiguration(string clientId)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OneDriveOAuth:ClientId"] = clientId,
                ["OneDriveOAuth:Tenant"] = "common"
            })
            .Build();

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string json)
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
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
