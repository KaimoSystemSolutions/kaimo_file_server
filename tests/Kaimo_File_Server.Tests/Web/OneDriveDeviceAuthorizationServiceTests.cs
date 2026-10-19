using System.Net;
using System.Text;
using Kaimo_File_Server.Infrastructure.Clouds;
using Kaimo_File_Server.Tests.Infrastructure;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class OneDriveDeviceAuthorizationServiceTests : DatabaseTestBase
{
    [Fact]
    public async Task DeviceFlow_UsesPublicClientWithoutSecretOrRedirectUri()
    {
        var requestBodies = new List<string>();
        var requestUris = new List<Uri>();
        var handler = new StubHttpMessageHandler(async request =>
        {
            requestBodies.Add(await request.Content!.ReadAsStringAsync());
            requestUris.Add(request.RequestUri!);
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
        var protection = new EphemeralDataProtectionProvider();
        var service = CreateService(http, protection);
        var secondInstance = CreateService(http, protection);
        var shareId = Guid.NewGuid();

        var authorization = await service.StartAsync(shareId, "documents", "ticket");
        var result = await secondInstance.PollAsync(authorization.SessionId);

        Assert.Equal("ABCD-EFGH", authorization.UserCode);
        Assert.Equal("https://microsoft.com/devicelogin", authorization.VerificationUri);
        Assert.Equal(OneDriveDevicePollState.Complete, result.State);
        Assert.Equal(shareId, result.ShareId);
        Assert.Equal("refresh-token", result.RefreshToken);
        Assert.All(requestBodies, body =>
            Assert.Contains($"client_id={OneDriveOAuthDefaults.ClientId}", body));
        Assert.All(requestUris, uri => Assert.Contains("/common/oauth2/v2.0/", uri.AbsolutePath));
        Assert.Contains("scope=offline_access+Files.ReadWrite+User.Read", requestBodies[0]);
        Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code", requestBodies[1]);
        Assert.All(requestBodies, body =>
        {
            Assert.DoesNotContain("client_secret", body);
            Assert.DoesNotContain("redirect_uri", body);
        });

        await using var db = await DbFactory.CreateDbContextAsync();
        Assert.Empty(await db.StorageDeviceAuthorizationSessions.ToListAsync());
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
        var service = CreateService(http);
        var authorization = await service.StartAsync(Guid.NewGuid(), "", "ticket");

        var result = await service.PollAsync(authorization.SessionId);

        Assert.Equal(OneDriveDevicePollState.Pending, result.State);
        Assert.Equal(5, result.RetryAfterSeconds);

        await using var db = await DbFactory.CreateDbContextAsync();
        var persisted = await db.StorageDeviceAuthorizationSessions.SingleAsync();
        Assert.DoesNotContain("device", persisted.ProtectedPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("ticket", persisted.ProtectedPayload, StringComparison.Ordinal);
    }

    private OneDriveDeviceAuthorizationService CreateService(
        HttpClient http,
        IDataProtectionProvider? protection = null)
        => new(
            new StubHttpClientFactory(http),
            DbFactory,
            protection ?? new EphemeralDataProtectionProvider(),
            TimeProvider.System);

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
