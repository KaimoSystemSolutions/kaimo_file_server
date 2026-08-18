using Google.Apis.Auth.OAuth2;

namespace Kaimo_File_Server.Infrastructure.Clouds;

/// <summary>
/// Creates scoped Google Workspace service-account credentials for an already
/// authorized first-class storage connection. Legacy share JSON must never
/// select this deployment-wide identity.
/// </summary>
public sealed class GoogleWorkspaceCredentialFactory(GoogleIdentityConfiguration identity)
{
    public bool IsConfigured => identity.WorkspaceIdentityEnabled;

    public GoogleCredential Create(GoogleDriveScopeProfile scopeProfile)
    {
        if (!identity.WorkspaceIdentityEnabled)
            throw new InvalidOperationException("Google Workspace identity is not configured.");

        var credential = CredentialFactory
            .FromJson<ServiceAccountCredential>(identity.WorkspaceCredentialJson!)
            .ToGoogleCredential()
            .CreateScoped(GoogleIdentityConfiguration.GetScopes(scopeProfile));
        return string.IsNullOrWhiteSpace(identity.WorkspaceImpersonatedSubject)
            ? credential
            : credential.CreateWithUser(identity.WorkspaceImpersonatedSubject);
    }
}
