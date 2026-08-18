using Kaimo_File_Server.Infrastructure.Clouds;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class GoogleIdentityConfigurationTests
{
    [Fact]
    public void SecretFileAndExternalBaseUrl_EnableDelegatedOAuth()
    {
        var secretFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(secretFile, "installation-secret\n");
            var identity = Load(new Dictionary<string, string?>
            {
                ["ExternalStorage:Google:ClientId"] = "client.apps.googleusercontent.com",
                ["ExternalStorage:Google:ClientSecretFile"] = Path.GetFullPath(secretFile),
                ["ExternalStorage:Google:ExternalBaseUrl"] = "https://files.example.test/kaimo/",
                ["ExternalStorage:Google:DefaultScopeProfile"] = "read-only"
            });

            Assert.True(identity.DelegatedOAuthEnabled);
            Assert.Equal(
                "https://files.example.test/kaimo/api/google/callback",
                identity.GetCallbackUri().AbsoluteUri);
            Assert.Equal(GoogleDriveScopeProfile.ReadOnly, identity.DefaultScopeProfile);
        }
        finally
        {
            File.Delete(secretFile);
        }
    }

    [Theory]
    [InlineData("SelectedItems", "https://www.googleapis.com/auth/drive.file")]
    [InlineData("ReadOnly", "https://www.googleapis.com/auth/drive.readonly")]
    [InlineData("ReadWrite", "https://www.googleapis.com/auth/drive")]
    public void ScopeProfiles_MapToOneExplicitDriveScope(string profile, string expectedScope)
    {
        var selected = GoogleIdentityConfiguration.ParseScopeProfile(profile);
        Assert.Equal([expectedScope], GoogleIdentityConfiguration.GetScopes(selected));
    }

    [Fact]
    public void PublicNonHttpsCallback_FailsClosed()
        => Assert.Throws<InvalidOperationException>(() => Load(new Dictionary<string, string?>
        {
            ["ExternalStorage:Google:ClientId"] = "client.apps.googleusercontent.com",
            ["ExternalStorage:Google:ClientSecret"] = "secret",
            ["ExternalStorage:Google:ExternalBaseUrl"] = "http://files.example.test"
        }));

    [Fact]
    public void ImpersonatedSubjectWithoutCredentialFile_FailsClosed()
        => Assert.Throws<InvalidOperationException>(() => Load(new Dictionary<string, string?>
        {
            ["ExternalStorage:Google:Workspace:ImpersonatedSubject"] = "sync@example.test"
        }));

    [Fact]
    public void ServiceAccountFileAndSubject_EnableWorkspaceIdentity()
    {
        var credentialFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(credentialFile, """
                {
                  "type": "service_account",
                  "client_email": "kaimo@example.test",
                  "private_key": "-----BEGIN PRIVATE KEY-----\\nplaceholder\\n-----END PRIVATE KEY-----\\n"
                }
                """);
            var identity = Load(new Dictionary<string, string?>
            {
                ["ExternalStorage:Google:Workspace:CredentialFile"] = Path.GetFullPath(credentialFile),
                ["ExternalStorage:Google:Workspace:ImpersonatedSubject"] = "sync@example.test"
            });

            Assert.True(identity.WorkspaceIdentityEnabled);
            Assert.Equal("sync@example.test", identity.WorkspaceImpersonatedSubject);
        }
        finally
        {
            File.Delete(credentialFile);
        }
    }

    [Fact]
    public void WorkspaceCredentialFactory_CreatesScopedImpersonatedCredential()
    {
        var credentialFile = Path.GetTempFileName();
        try
        {
            using var rsa = RSA.Create(2048);
            File.WriteAllText(credentialFile, JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["type"] = "service_account",
                ["project_id"] = "kaimo-test",
                ["private_key_id"] = "test-key",
                ["private_key"] = rsa.ExportPkcs8PrivateKeyPem(),
                ["client_email"] = "kaimo@kaimo-test.iam.gserviceaccount.com",
                ["client_id"] = "1234567890",
                ["token_uri"] = "https://oauth2.googleapis.com/token"
            }));
            var identity = Load(new Dictionary<string, string?>
            {
                ["ExternalStorage:Google:Workspace:CredentialFile"] = Path.GetFullPath(credentialFile),
                ["ExternalStorage:Google:Workspace:ImpersonatedSubject"] = "sync@example.test"
            });
            var factory = new GoogleWorkspaceCredentialFactory(identity);

            var credential = factory.Create(GoogleDriveScopeProfile.ReadOnly);

            Assert.True(factory.IsConfigured);
            Assert.NotNull(credential);
        }
        finally
        {
            File.Delete(credentialFile);
        }
    }

    [Fact]
    public void AuthorizationRequest_UsesOpaqueStatePkceAndConfiguredScope()
    {
        var identity = Load(new Dictionary<string, string?>
        {
            ["ExternalStorage:Google:ClientId"] = "client.apps.googleusercontent.com",
            ["ExternalStorage:Google:ClientSecret"] = "secret",
            ["ExternalStorage:Google:ExternalBaseUrl"] = "https://files.example.test"
        });
        var service = new GoogleOAuthService(
            identity,
            new GoogleOAuthClientFactory(identity),
            new EphemeralDataProtectionProvider());

        var start = service.BeginAuthorization("opaque-state", GoogleDriveScopeProfile.ReadOnly);
        var uri = new Uri(start.AuthorizationUri);
        var query = QueryHelpers.ParseQuery(uri.Query);

        Assert.Equal("opaque-state", query["state"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.False(string.IsNullOrWhiteSpace(query["code_challenge"]));
        Assert.Equal("offline", query["access_type"]);
        Assert.Equal("consent", query["prompt"]);
        Assert.Equal("https://www.googleapis.com/auth/drive.readonly", query["scope"]);
        Assert.Equal(
            "https://files.example.test/api/google/callback",
            query["redirect_uri"]);
        Assert.DoesNotContain("secret", start.AuthorizationUri, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", start.ProtectedContext, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadOnlyProfile_BlocksWritesBeforeProviderRequest()
    {
        var identity = Load(new Dictionary<string, string?>
        {
            ["ExternalStorage:Google:ClientId"] = "client.apps.googleusercontent.com",
            ["ExternalStorage:Google:ClientSecret"] = "secret",
            ["ExternalStorage:Google:ExternalBaseUrl"] = "https://files.example.test"
        });
        var connection = new GoogleDriveConnection(
            Guid.NewGuid(),
            new Dictionary<string, string>
            {
                ["refreshToken"] = "refresh",
                ["scopeProfile"] = "ReadOnly"
            },
            identity);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                connection.CreateDirectoryAsync("must-not-be-created"));
        }
        finally
        {
            connection.Service.Dispose();
        }
    }

    private static GoogleIdentityConfiguration Load(Dictionary<string, string?> values)
        => GoogleIdentityConfiguration.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build());
}
