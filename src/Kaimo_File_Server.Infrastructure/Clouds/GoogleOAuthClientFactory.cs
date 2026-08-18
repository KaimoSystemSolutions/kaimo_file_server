using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;

namespace Kaimo_File_Server.Infrastructure.Clouds;

/// <summary>
/// Creates maintained Google OAuth clients without exposing the deployment
/// client secret outside the infrastructure boundary.
/// </summary>
public sealed class GoogleOAuthClientFactory(GoogleIdentityConfiguration identity)
{
    public PkceGoogleAuthorizationCodeFlow Create(GoogleDriveScopeProfile scopeProfile)
    {
        if (!identity.DelegatedOAuthEnabled)
            throw new InvalidOperationException("Google delegated OAuth is not configured.");

        return new PkceGoogleAuthorizationCodeFlow(
            new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = new ClientSecrets
                {
                    ClientId = identity.ClientId,
                    ClientSecret = identity.ClientSecret
                },
                Scopes = GoogleIdentityConfiguration.GetScopes(scopeProfile)
            });
    }
}
