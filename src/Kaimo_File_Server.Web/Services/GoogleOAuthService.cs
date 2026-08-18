using System.Security.Cryptography;
using System.Text.Json;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Requests;
using Kaimo_File_Server.Infrastructure.Clouds;
using Microsoft.AspNetCore.DataProtection;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Creates Google authorization requests and exchanges callback codes through
/// Google's maintained OAuth client, including PKCE and protected callback state.
/// </summary>
public sealed class GoogleOAuthService
{
    private readonly GoogleIdentityConfiguration _identity;
    private readonly GoogleOAuthClientFactory _clientFactory;
    private readonly IDataProtector _protector;

    public GoogleOAuthService(
        GoogleIdentityConfiguration identity,
        GoogleOAuthClientFactory clientFactory,
        IDataProtectionProvider dataProtectionProvider)
    {
        _identity = identity;
        _clientFactory = clientFactory;
        _protector = dataProtectionProvider.CreateProtector(
            "KaimoFiles.ExternalStorage.GoogleOAuth", "v1");
    }

    /// <summary>Builds an offline-access authorization request with an S256 PKCE challenge.</summary>
    public GoogleAuthorizationStart BeginAuthorization(
        string opaqueState,
        GoogleDriveScopeProfile scopeProfile)
    {
        if (!_identity.DelegatedOAuthEnabled)
            throw new InvalidOperationException("Google delegated OAuth is not configured.");
        ArgumentException.ThrowIfNullOrWhiteSpace(opaqueState);

        var redirectUri = _identity.GetCallbackUri().AbsoluteUri;
        using var flow = CreateFlow(scopeProfile);
        var request = (GoogleAuthorizationCodeRequestUrl)flow.CreateAuthorizationCodeRequest(
            redirectUri,
            out var codeVerifier);
        request.State = opaqueState;
        request.AccessType = "offline";
        request.Prompt = "consent";

        var context = new GoogleAuthorizationContext(
            redirectUri,
            codeVerifier,
            scopeProfile,
            GoogleIdentityConfiguration.GetScopes(scopeProfile).ToArray());
        return new GoogleAuthorizationStart(
            request.Build().AbsoluteUri,
            _protector.Protect(JsonSerializer.Serialize(context)));
    }

    /// <summary>Validates protected callback context and exchanges a code through Google APIs.</summary>
    public async Task<GoogleAuthorizationGrant> CompleteAuthorizationAsync(
        string userId,
        string authorizationCode,
        string protectedContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationCode);

        GoogleAuthorizationContext context;
        try
        {
            context = JsonSerializer.Deserialize<GoogleAuthorizationContext>(
                          _protector.Unprotect(protectedContext))
                      ?? throw new CryptographicException("Google authorization context is invalid.");
        }
        catch (JsonException exception)
        {
            throw new CryptographicException("Google authorization context is invalid.", exception);
        }

        var expectedRedirectUri = _identity.GetCallbackUri().AbsoluteUri;
        var expectedScopes = GoogleIdentityConfiguration.GetScopes(context.ScopeProfile);
        if (!string.Equals(context.RedirectUri, expectedRedirectUri, StringComparison.Ordinal)
            || context.Scopes.Length != expectedScopes.Count
            || !context.Scopes.SequenceEqual(expectedScopes, StringComparer.Ordinal))
            throw new CryptographicException("Google authorization context binding is invalid.");

        using var flow = CreateFlow(context.ScopeProfile);
        var response = await flow.ExchangeCodeForTokenAsync(
            userId,
            authorizationCode,
            context.CodeVerifier,
            context.RedirectUri,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(response.RefreshToken))
            throw new InvalidOperationException("Google did not return an offline refresh grant.");

        var grantedScopes = string.IsNullOrWhiteSpace(response.Scope)
            ? string.Join(' ', expectedScopes)
            : response.Scope;
        return new GoogleAuthorizationGrant(
            response.RefreshToken,
            grantedScopes,
            context.ScopeProfile);
    }

    private PkceGoogleAuthorizationCodeFlow CreateFlow(GoogleDriveScopeProfile scopeProfile)
        => _clientFactory.Create(scopeProfile);

    private sealed record GoogleAuthorizationContext(
        string RedirectUri,
        string CodeVerifier,
        GoogleDriveScopeProfile ScopeProfile,
        string[] Scopes);
}

/// <summary>Browser redirect plus server-only protected callback state.</summary>
public sealed record GoogleAuthorizationStart(string AuthorizationUri, string ProtectedContext);

/// <summary>Long-lived Google grant material returned by a completed authorization.</summary>
public sealed record GoogleAuthorizationGrant(
    string RefreshToken,
    string GrantedScopes,
    GoogleDriveScopeProfile ScopeProfile);
