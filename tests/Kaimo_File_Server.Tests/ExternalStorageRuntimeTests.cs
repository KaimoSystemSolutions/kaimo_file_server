using System.Net;
using System.Text.Json;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Repositories;
using Kaimo_File_Server.Infrastructure.Services;
using Kaimo_File_Server.Tests.Infrastructure;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class ExternalStorageRuntimeTests : DatabaseTestBase
{
    [Fact]
    public async Task CredentialLease_IsExclusiveAcrossManagersAndReleasesForNextOwner()
    {
        var connection = await SeedConnectionAsync();
        var firstManager = new DatabaseStorageConnectionCredentialLeaseManager(DbFactory, TimeProvider.System);
        var secondManager = new DatabaseStorageConnectionCredentialLeaseManager(DbFactory, TimeProvider.System);

        var first = await firstManager.TryAcquireAsync(connection.Id);
        Assert.NotNull(first);
        Assert.Null(await secondManager.TryAcquireAsync(connection.Id));

        await first.DisposeAsync();
        var second = await secondManager.TryAcquireAsync(connection.Id);
        Assert.NotNull(second);
        await second.DisposeAsync();
    }

    [Fact]
    public async Task CredentialLease_ReclaimsExpiredOwnerAfterCrash()
    {
        var connection = await SeedConnectionAsync();
        await using (var db = await DbFactory.CreateDbContextAsync())
        {
            db.StorageConnectionCredentialLeases.Add(new StorageConnectionCredentialLease
            {
                ConnectionId = connection.Id,
                LeaseId = Guid.NewGuid(),
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1)
            });
            await db.SaveChangesAsync();
        }

        var manager = new DatabaseStorageConnectionCredentialLeaseManager(DbFactory, TimeProvider.System);
        var lease = await manager.TryAcquireAsync(connection.Id);

        Assert.NotNull(lease);
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task RewrapBatch_UpgradesLegacyPayloadWithoutChangingPlaintext()
    {
        var provider = new EphemeralDataProtectionProvider();
        var legacyProtector = provider.CreateProtector("KaimoFiles.CloudAccess.Credentials", "v1");
        var credentials = new Dictionary<string, string> { ["refreshToken"] = "legacy-secret" };
        var legacyPayload = "dp:v1:" + legacyProtector.Protect(JsonSerializer.Serialize(credentials));
        var connection = await SeedConnectionAsync(legacyPayload);
        var repository = new StorageConnectionRepository(DbFactory);
        var vault = new DataProtectionCredentialVault(provider);
        var leases = new DatabaseStorageConnectionCredentialLeaseManager(DbFactory, TimeProvider.System);
        var service = new CredentialRewrapService(repository, vault, leases);

        var result = await service.RewrapBatchAsync();
        var updated = await repository.GetAsync(connection.Id);

        Assert.Equal(1, result.Rewrapped);
        Assert.StartsWith("dp:v2:", updated!.EncryptedCredentialPayload);
        Assert.Equal(StorageConnection.CurrentProtectorPurposeVersion, updated.ProtectorPurposeVersion);
        Assert.Equal(credentials, vault.UnprotectConnectionCredentials(updated));
    }

    private async Task<StorageConnection> SeedConnectionAsync(string? encryptedPayload = null)
    {
        var connection = new StorageConnection
        {
            CreatedByUserId = Guid.NewGuid(),
            ProviderId = "onedrive",
            Name = "Connection " + Guid.NewGuid().ToString("N"),
            EncryptedCredentialPayload = encryptedPayload,
            ProtectorPurposeVersion = encryptedPayload is null ? 2 : 1,
            State = StorageConnectionState.Ready
        };
        var repository = new StorageConnectionRepository(DbFactory);
        await repository.SaveAsync(connection);
        return connection;
    }
}

public sealed class ProviderErrorSanitizerTests
{
    [Fact]
    public void Redact_RemovesJsonFormBearerAndJwtSecrets()
    {
        const string refreshToken = "refresh-secret";
        const string clientSecret = "client-secret";
        const string bearerToken = "bearer-secret";
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.signature";
        var input = $$"""
            {"refresh_token":"{{refreshToken}}","safe":"value"}
            client_secret={{clientSecret}}&scope=files Bearer {{bearerToken}} {{jwt}}
            """;

        var redacted = ProviderErrorSanitizer.Redact(input);

        Assert.DoesNotContain(refreshToken, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(clientSecret, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(bearerToken, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(jwt, redacted, StringComparison.Ordinal);
        Assert.Contains("value", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void FromResponse_ExtractsOnlySafeCodeAndDropsProviderBody()
    {
        const string secret = "do-not-retain";
        var error = ProviderErrorSanitizer.FromResponse(
            HttpStatusCode.BadRequest,
            $$"""{"error":"invalid_grant","error_description":"token {{secret}}"}""");
        var exception = new ProviderRequestException("provider", error);

        Assert.Equal("invalid_grant", error.Code);
        Assert.Equal(ProviderErrorCategory.Authentication, error.Category);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        // Structured provider errors expose nothing but their code.
        Assert.Null(error.Detail);
    }

    [Fact]
    public void FromResponse_KeepsRedactedDetailForRouteLevelBody()
    {
        // Dropbox returns a plaintext (non-JSON) body for transport/route-level
        // failures such as an empty Authorization header. Without the detail this
        // collapses to a bare "http_400" that hides the real cause.
        const string message =
            "Error in call to API function \"files/list_folder\": " +
            "Invalid authorization value in HTTP header/URL parameter";
        var error = ProviderErrorSanitizer.FromResponse(HttpStatusCode.BadRequest, message);
        var exception = new ProviderRequestException("dropbox", error);

        Assert.Equal("http_400", error.Code);
        Assert.Equal(message, error.Detail);
        Assert.Contains("Invalid authorization value", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromResponse_RedactsSecretsInRouteLevelDetail()
    {
        const string token = "sl.SuperSecretAccessTokenValue";
        var error = ProviderErrorSanitizer.FromResponse(
            HttpStatusCode.BadRequest, $"unexpected framing error near Bearer {token}");
        var exception = new ProviderRequestException("dropbox", error);

        Assert.Equal("http_400", error.Code);
        Assert.NotNull(error.Detail);
        Assert.DoesNotContain(token, exception.ToString(), StringComparison.Ordinal);
    }
}
